using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
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
    Func<ChatStreamEvent, Task>? StreamSink = null) : IRequest<Result<ChatResponse>>;

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
    IReadOnlyList<string>? FollowUps = null);

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
    AccountRowsCard? AccountRows = null)
{
    public bool HasAnyCard => HabitList is not null || GoalList is not null
        || MetricsCard is not null || PeriodInsight is not null || DaySummary is not null
        || Streak is not null || Calendar is not null || RecordLists is { Count: > 0 }
        || AccountRows is not null;
}

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
    IPendingClarificationStore PendingClarificationStore,
    IGoalProgressReadSyncer GoalProgressReadSyncer,
    IGamificationService GamificationService,
    IMediator Mediator,
    IProductAnalytics ProductAnalytics);

public partial class ProcessUserChatCommandHandler(
    ChatDataDependencies data,
    ChatAiDependencies ai,
    ChatExecutionDependencies execution,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;
    private const string UnsupportedByPolicyReason = "unsupported_by_policy";
    private const string DescribeFeatureToolName = "describe_feature";
    private const string EnglishToolFailureMessage = "I couldn't complete that. Please try again.";
    private const string PortugueseToolFailureMessage = "Não consegui concluir isso. Tente novamente.";
    private const string EnglishTruncationMessage = "This response was cut off before completion, so the result is partial.";
    private const string PortugueseTruncationMessage = "Esta resposta foi interrompida antes da conclusão, então o resultado é parcial.";

    private const int MaxSupportMessageLength = 5000;

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);
        var detectedCrisisLocales = CrisisSupportGuard.Detect(request.Message);

        var contextResult = await LoadChatContextAsync(request, cancellationToken);
        if (contextResult.IsFailure)
            return contextResult.PropagateError<ChatResponse>();

        var context = contextResult.Value;
        var crisisTurn = IsCrisisTurn(detectedCrisisLocales, context.EnabledFeatureFlags);
        if (crisisTurn)
            return await HandleCrisisTurnAsync(request, detectedCrisisLocales);

        request = ApplyClientKillFlags(request, context.EnabledFeatureFlags);
        CaptureFollowUpSentEvent(request, context.User);

        var userLanguage = GetUserLanguage(context.User);
        var aiStreamFilter = BuildAiStreamFilter(request.StreamSink);
        Func<AiStreamEvent, Task>? aiStreamSink = aiStreamFilter is null
            ? null
            : aiStreamFilter.HandleAsync;

        var faqMatch = ChatFaqCache.TryMatchFaqKey(request.Message);
        var cachedFaqAnswer = TryGetCachedFaqAnswer(faqMatch);
        if (cachedFaqAnswer is not null)
            return Result.Success(new ChatResponse(cachedFaqAnswer, [], CorrelationId: request.CorrelationId));

        var skipTools = request.ImageData is null
            && request.ConfirmationToken is null
            && (request.History is null || request.History.Count == 0)
            && ChatIntentRouter.IsNoToolTurn(
                request.Message,
                ChatToolGroups.IsKnownEntryPointIntent(GetEntryPointIntent(request)));

        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await RequestInitialAiResponseAsync(request, context, aiStreamSink, skipTools, cancellationToken);
        aiStopwatch.Stop();
        LogAiIntentServiceCompleted(logger, aiStopwatch.ElapsedMilliseconds);

        if (response.IsFailure)
            return response.PropagateError<ChatResponse>();

        var executionResults = new ToolExecutionAccumulator();
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolLoopResult = await RunToolCallLoopAsync(
            response.Value,
            request,
            executionResults,
            userLanguage,
            aiStreamSink,
            cancellationToken);
        var aiResponse = toolLoopResult.FinalResponse;
        var iterations = toolLoopResult.Iterations;
        if (toolLoopResult.HadToolFailure)
            executionResults.SanitizeFailedActions(ToolFailureMessage(userLanguage));
        await EmitToolStepsAsync(request, toolLoopResult, executionResults);
        if (aiStreamFilter is not null)
            await aiStreamFilter.FlushAsync();
        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iterations, executionResults.ActionResults.Count);

        LogSavingChanges(logger);
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await PersistExecutionResultsAsync(request.UserId, executionResults.ActionResults, cancellationToken);
        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

        if (IsEmptyTokenBudgetResponse(toolLoopResult.TokenBudgetExceeded, aiResponse.TextMessage))
            return Result.Failure<ChatResponse>(ErrorMessages.AiUnavailable);

        var (responseText, parsedFollowUps) = FollowUpDirective.Extract(StripJsonWrapper(aiResponse.TextMessage));
        var followUps = CanEmitFollowUps(request, toolLoopResult, executionResults, aiResponse)
            ? parsedFollowUps
            : null;
        if (aiResponse.IsTruncated)
            responseText = AppendTruncationNotice(responseText, userLanguage);
        var cards = await TryBuildResponseCardsAsync(
            responseText, request, context, executionResults, cancellationToken);
        var aiMessage = cards.AiMessage;

        if (GetShareableFaqAnswer(faqMatch, aiMessage, executionResults, cards) is { } faqToCache)
        {
            ChatFaqCache.StoreAnswer(faqToCache.Key, faqToCache.Locale, faqToCache.Answer);
        }

        CaptureResponseCardEvents(request, context, cards);

        CaptureChangePreviewEvents(request, context.User, executionResults.PendingOperations);
        CaptureFollowUpsEmittedEvent(request, context.User, followUps);

        RunBackgroundPostResponseWork(
            request.UserId,
            request.Message,
            aiMessage,
            shouldExtractFacts: ShouldExtractFacts(context),
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
            followUps));
    }

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
        && results.PendingOperations.Count == 0
        && !results.ActionResults.Any(action => action.Status == ActionStatus.NeedsClarification);

    private static async Task EmitToolStepsAsync(
        ProcessUserChatCommand request,
        ToolLoopResult loopResult,
        ToolExecutionAccumulator results)
    {
        if (request.StreamSink is null || request.ClientContext?.SupportsToolSteps != true
            || loopResult.HadToolFailure || loopResult.TokenBudgetExceeded
            || loopResult.FinalResponse.IsTruncated || results.PendingOperations.Count > 0
            || results.ActionResults.Any(action => action.Status == ActionStatus.NeedsClarification))
            return;

        foreach (var (domain, access) in results.ToolSteps)
            await request.StreamSink(ChatStreamEvent.Step(domain, access));
    }

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

    private static bool IsCrisisTurn(CrisisLocales locales, IReadOnlyList<string> enabledFlags) =>
        locales != CrisisLocales.None
        && !enabledFlags.Contains(FeatureFlagKeys.AstraCrisisResourcesDisabled, StringComparer.OrdinalIgnoreCase);

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

    private static bool ShouldExtractFacts(ChatContext context) =>
        context.AiMemoryEnabled && context.User is { HasProAccess: true };

    private void CaptureCardEvent(
        ProcessUserChatCommand request, ChatContext context, string eventName, string kind)
    {
        if (context.User is null)
            return;

        AnalyticsCapture.SafeCaptureUserEvent(
            execution.ProductAnalytics, logger, request.UserId, context.User.Plan.ToString(),
            eventName, new Dictionary<string, object>
            {
                ["platform"] = request.ClientContext?.Platform ?? "unknown",
                ["kind"] = kind
            });
    }

    private void CaptureResponseCardEvents(
        ProcessUserChatCommand request, ChatContext context, ResponseCards cards)
    {
        if (cards.MetricsCard is not null)
            CaptureCardEvent(request, context, "chat_metrics_card_emitted", "metrics");
        if (cards.PeriodInsight is not null)
            CaptureCardEvent(request, context, "chat_period_insight_card_emitted", "period");
        if (cards.DaySummary is not null)
            CaptureCardEvent(request, context, "chat_day_summary_card_emitted", "day");
        if (cards.Streak is not null)
            CaptureCardEvent(request, context, "chat_streak_card_emitted", "streak");
        if (cards.Calendar is not null)
            CaptureCardEvent(request, context, "chat_calendar_card_emitted", "calendar");
        foreach (var recordList in cards.RecordLists ?? [])
            CaptureCardEvent(request, context, "chat_record_list_card_emitted", recordList.Kind);
        if (cards.AccountRows is not null)
            CaptureCardEvent(request, context, "chat_account_rows_card_emitted", cards.AccountRows.Kind);
    }

    private async Task<Result<ChatResponse>> HandleCrisisTurnAsync(
        ProcessUserChatCommand request,
        CrisisLocales locales)
    {
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

    private async Task<ResponseCards> TryBuildResponseCardsAsync(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator executionResults, CancellationToken cancellationToken)
    {
        try
        {
            return await BuildResponseCardsAsync(aiMessage, request, context, executionResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            return new ResponseCards(StripAllDirectives(aiMessage));
        }
    }

    private static string? StripAllDirectives(string? message) =>
        message is null ? null : Regex.Replace(message, @"\[\[orbit:[a-z:]+\]\]", string.Empty,
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();

    private async Task<ResponseCards> BuildResponseCardsAsync(
        string? aiMessage,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        CancellationToken cancellationToken)
    {
        HabitListCard? habitList = null;
        if (HabitListCardBuilder.TryExtractScope(aiMessage, out var habitListScope, out var strippedMessage))
        {
            aiMessage = strippedMessage;
            if (request.ClientContext?.SupportsHabitListCard == true)
                habitList = HabitListCardBuilder.Build(context.ActiveHabits, context.UserToday, habitListScope);
        }

        GoalListCard? goalList = null;
        if (GoalListCardBuilder.TryExtractDirective(aiMessage, out var strippedGoalMessage))
        {
            aiMessage = strippedGoalMessage;
            if (request.ClientContext?.SupportsGoalListCard == true)
            {
                var includeProjections = !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraGoalProjectionsDisabled);
                goalList = executionResults.LastSuccessfulPayload<GoalListCard>("review_goals")
                    ?? GoalListCardBuilder.Build(
                        context.ActiveGoals, context.UserToday, context.User?.WeekStartDay ?? 1,
                        includeProjections);
                if (!includeProjections)
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
                    ? await TryBuildHabitMetricsCardAsync(request.UserId, context.UserToday, context.User?.TimeZone,
                        habitMetrics, cancellationToken)
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

        var (daySummary, streak, calendar) = await BuildStatusCardsAsync(
            aiMessage, request, context, executionResults, cancellationToken);
        var recordLists = BuildRecordLists(aiMessage, request, context, executionResults);
        var accountRows = await TryBuildAccountRowsAsync(
            aiMessage, request, context, executionResults, cancellationToken);

        aiMessage = StripAllDirectives(aiMessage);
        return new ResponseCards(aiMessage, habitList, goalList, metricsCard, periodInsight,
            daySummary, streak, calendar, recordLists, accountRows);
    }

    private async Task<AccountRowsCard?> TryBuildAccountRowsAsync(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken cancellationToken)
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

        try
        {
            var profileResult = await execution.Mediator.Send(new GetProfileQuery(request.UserId), cancellationToken);
            return profileResult.IsSuccess ? AccountRowsCardBuilder.Plan(profileResult.Value) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            return null;
        }
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
                    ? RecordListCardBuilder.BuildNotifications(notifications) : null,
                "tags" => results.LastSuccessfulPayload<IReadOnlyList<TagResponse>>("list_tags") is { } tags
                    ? RecordListCardBuilder.BuildTags(tags) : null,
                "templates" => results.LastSuccessfulPayload<IReadOnlyList<ChecklistTemplateResponse>>("get_checklist_templates") is { } templates
                    ? RecordListCardBuilder.BuildTemplates(templates) : null,
                "keys" => results.LastSuccessfulPayload<IReadOnlyList<ApiKeyResponse>>("get_api_keys") is { } keys
                    ? RecordListCardBuilder.BuildKeys(keys, TimeProvider.System.GetUtcNow().UtcDateTime) : null,
                _ => null
            };
            if (card is not null)
                cards.Add(card);
        }

        return cards.Count == 0 ? null : cards;
    }

    private async Task<(DaySummaryCard? Day, StreakCard? Streak, CalendarCard? Calendar)> BuildStatusCardsAsync(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator executionResults, CancellationToken cancellationToken)
    {
        if (context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraStatusBlocksDisabled))
            return (null, null, null);

        DaySummaryCard? day = null;
        if (request.ClientContext?.SupportsDaySummaryCard == true
            && aiMessage?.Contains(StatusCardBuilder.DayDirective, StringComparison.OrdinalIgnoreCase) == true
            && executionResults.LastSuccessfulPayload<DailySummaryResponse>("get_daily_summary") is not null)
        {
            day = await TryBuildDaySummaryCardAsync(request.UserId, context.UserToday,
                context.User?.CurrentStreak ?? 0, context.User?.TimeZone, cancellationToken);
        }

        var streak = request.ClientContext?.SupportsStreakCard == true
            && aiMessage?.Contains(StatusCardBuilder.StreakDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildStreak(
                executionResults.LastSuccessfulPayload<GamificationOverviewPayload>("get_gamification_overview"))
            : null;
        var calendar = request.ClientContext?.SupportsCalendarCard == true
            && aiMessage?.Contains(StatusCardBuilder.CalendarDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildCalendar(
                executionResults.LastSuccessfulPayload<CalendarOverviewPayload>("get_calendar_overview"))
            : null;
        return (day, streak, calendar);
    }

    private async Task<DaySummaryCard?> TryBuildDaySummaryCardAsync(
        Guid userId, DateOnly userToday, int currentStreak, string? timeZone, CancellationToken cancellationToken)
    {
        try
        {
            var habits = await data.HabitRepository.FindAsync(
                h => h.UserId == userId,
                q => q.Include(h => h.Logs.Where(log => log.Date == userToday)), cancellationToken);
            var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
            var metrics = RetrospectiveMetricsCalculator.ComputeHistorical(
                habits.ToList(), userToday, userToday, currentStreak, currentStreak,
                TimeZoneHelper.FindTimeZone(timeZone), weekStartDay);
            var overdue = habits.Count(h => !h.IsGeneral && !h.IsCompleted && h.DueDate < userToday);
            return StatusCardBuilder.BuildDay(userToday, metrics, overdue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            return null;
        }
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
            var recapResult = await execution.Mediator.Send(
                new GetRecapQuery(userId, dateFrom, dateTo, "week"),
                cancellationToken);

            if (recapResult.IsFailure)
            {
                LogMetricsCardBuildFailed(logger, recapResult.Error);
                return null;
            }

            return MetricsCardBuilder.Build(recapResult.Value.Period, recapResult.Value.Metrics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMetricsCardBuildThrew(logger, ex);
            return null;
        }
    }

    private async Task<MetricsCard?> TryBuildHabitMetricsCardAsync(
        Guid userId,
        DateOnly userToday,
        string? timeZone,
        HabitMetrics habitMetrics,
        CancellationToken cancellationToken)
    {
        try
        {
            var dateFrom = userToday.AddDays(-29);
            var habit = await data.HabitRepository.FindOneTrackedAsync(
                h => h.UserId == userId && h.Id == habitMetrics.HabitId,
                q => q.Include(h => h.Logs.Where(l => l.Date >= dateFrom && l.Date <= userToday)),
                cancellationToken);
            if (habit is null)
                return null;

            var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
            var periodMetrics = RetrospectiveMetricsCalculator.ComputeHistorical(
                [habit], dateFrom, userToday, habitMetrics.CurrentStreak, habitMetrics.LongestStreak,
                TimeZoneHelper.FindTimeZone(timeZone), weekStartDay);
            return MetricsCardBuilder.BuildHabit(habitMetrics, periodMetrics);
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
        && results.PendingOperations.Count == 0
        && results.CalledToolNames.All(name => name == DescribeFeatureToolName)
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Processing chat message: '{Message}'")]
    private static partial void LogProcessingChatMessage(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Context loaded in {ElapsedMs}ms (Habits: {HabitCount}, Facts: {FactCount})")]
    private static partial void LogContextLoaded(ILogger logger, long elapsedMs, int habitCount, int factCount);

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

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Background post-response work failed")]
    private static partial void LogBackgroundPostResponseFailed(ILogger logger, Exception ex);

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

    [LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "Metrics card could not be built: {Error}")]
    private static partial void LogMetricsCardBuildFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Metrics card computation failed")]
    private static partial void LogMetricsCardBuildThrew(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Response card build failed")]
    private static partial void LogResponseCardBuildThrew(ILogger logger, Exception ex);
}
