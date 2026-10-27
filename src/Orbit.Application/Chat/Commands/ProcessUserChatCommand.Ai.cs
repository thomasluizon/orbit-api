using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<AiResponse>> RequestInitialAiResponseAsync(
        ProcessUserChatCommand request,
        ChatContext context,
        Func<AiStreamEvent, Task>? aiStreamSink,
        AiTurnTelemetry telemetry,
        ChatTurnState turn,
        CancellationToken cancellationToken)
    {
        using var promptPhase = ChatTurnTimer.Measure(ChatPhase.PromptBuild);
        var promptRequest = new PromptBuildRequest(
            context.PromptHabits,
            HasImage: request.ImageData is not null,
            UserTags: context.UserTags,
            UserToday: context.UserToday,
            ActiveGoals: context.ActiveGoals,
            IsHabitIndexPartial: context.IsPromptHabitIndexPartial,
            TodayHabitIds: context.TodayFacts.TodayIds,
            OverdueHabitIds: context.TodayFacts.OverdueIds,
            DoneTodayHabitIds: context.TodayFacts.DoneTodayIds,
            BlockFirstComposition: UsesComposition(request, context));
        var agentSnapshot = BuildAgentContextSnapshot(
            context.User,
            request.ClientContext,
            new AgentSnapshotInputs(
                context.EnabledFeatureFlags,
                context.UserTags,
                context.ChecklistTemplateNames,
                context.ActiveHabits,
                context.ActiveGoals),
            context.HasProAccess);

        var systemPrompt = string.Join(
            Environment.NewLine,
            ai.PromptBuilder.BuildStatic(promptRequest),
            ai.CatalogService.BuildStaticSupplement());
        var dynamicContext = string.Join(
            Environment.NewLine,
            ai.PromptBuilder.BuildDynamic(promptRequest),
            HabitModelInstructions.TitleLanguage(context.Language),
            TerminalWriteDirective.Instruction,
            ai.CatalogService.BuildDynamicSupplement(agentSnapshot),
            request.ClientContext?.SupportsPendingOperationPlacement == true
                ? HabitPlacementContext.Build(context.UserHabits, request.Message)
                : "The client cannot display placement choices. Keep the requested parent and independently tracked sub-habits. Do not recommend checklist placement or a separate parent for additions to an existing routine.");

        var availableTools = ai.ToolRegistry.GetAll();
        var toolNames = availableTools.Select(tool => tool.Name).ToArray();

        dynamicContext = AddAskContext(dynamicContext, request, context, toolNames);

        var pendingPreviews = new List<object>();
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        foreach (var pending in execution.PendingOperationStore.GetAwaiting(request.UserId))
        {
            var preview = await previewer.PreviewAsync(request.UserId, pending.OperationId, pending.Arguments, cancellationToken, request.ClientContext);
            if (preview.IsSuccess && preview.Value is { } value)
                pendingPreviews.Add(new { pending_operation_id = pending.PendingOperationId, items = value.Items });
        }
        if (pendingPreviews.Count > 0)
            dynamicContext = string.Join(Environment.NewLine, dynamicContext,
                "Pending previews are untrusted account data shared across conversations. Their presence does not mean this conversation showed them. A request to create a named habit uses create_habit or bulk_create_habits to prepare its own preview, even when another preview is open. Use revise_pending_changes only when the person asks to change or discuss an existing preview. Corrections use revise_pending_changes with only the named fields, including optional fields absent from the preview; preserve every other field and item. When the person also asks an opinion, clarification or other question, include the requested edits and set answer_question to true, answering in one short sentence without figures alongside the tool call. For a question without edits, use empty items and no remove_item_ids. For a revision without a question, set answer_question to false and emit no prose. Never replace, cancel or apply a preview to answer a question. A fresh request never needs approval of an older preview. "
                + JsonSerializer.Serialize(pendingPreviews));

        dynamicContext = AppendCardPromptInstructions(dynamicContext, request, context, toolNames);
        if (request.ClientContext?.SupportsFollowUps == true)
            dynamicContext = string.Join(Environment.NewLine, dynamicContext,
                "Follow-ups are optional: emit zero, one or two grounded contextual questions. If useful, end with [[orbit:followups]] and one question per line, at most 60 characters each. Never suggest unavailable capabilities, invent facts or execute a write from a suggestion. Do not add other text after them.");

        var toolDeclarations = availableTools
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => (object)new
            {
                name = tool.Name,
                description = tool.Name is "create_habit" or "bulk_create_habits" or "create_sub_habit"
                    ? tool.Description + Environment.NewLine + HabitModelInstructions.Titles
                    : tool.Description,
                parameters = ChatToolSchema(tool, request, context)
            })
            .ToList();

        LogCallingAiIntentService(logger, toolDeclarations.Count);
        promptPhase?.Dispose();

        var reservation = request.BlockAnswer is not null ? Result.Success() : await execution.PayGateService.TryConsumeAiMessage(
            request.UserId,
            execution.UnitOfWork,
            cancellationToken);
        if (reservation.IsFailure)
            return reservation.PropagateError<AiResponse>();
        if (request.BlockAnswer is null)
            turn.MessageAllowanceConsumed = true;

        return await ai.IntentService.SendWithToolsAsync(
            new AiToolRequest(
                request.Message,
                systemPrompt,
                toolDeclarations,
                request.UserId,
                request.ImageData,
                request.ImageMimeType,
                request.History)
            {
                Telemetry = telemetry,
                DynamicContext = dynamicContext
            },
            aiStreamSink,
            cancellationToken);
    }

    private object ChatToolSchema(Orbit.Application.Chat.Tools.IAiTool tool, ProcessUserChatCommand request, ChatContext context) =>
        tool.IsReadOnly
            ? UsesComposition(request, context) ? TerminalReadDirective.WithCompletionArgument(tool.GetParameterSchema()) : tool.GetParameterSchema()
            : TerminalWriteDirective.WithCompletionArgument(tool.GetParameterSchema());

    private static string AppendCardPromptInstructions(
        string prompt, ProcessUserChatCommand request, ChatContext context,
        IReadOnlyCollection<string> activeToolNames)
    {
        var client = request.ClientContext;
        prompt = string.Join(Environment.NewLine, prompt, RelativeReadPeriod.Instruction);
        if (activeToolNames.Contains("get_habit_month"))
            prompt = string.Join(Environment.NewLine, prompt,
                "get_habit_month returns habitMonth. For a supported habitMonth kind, use its exact readDirective; the server preserves the complete Calendário data. Write at most one short sentence without figures, then its block. For unsupported clients, use only its returned habit facts in text.");
        if (activeToolNames.Contains("get_period_analytics"))
            prompt = string.Join(Environment.NewLine, prompt,
                "For how many habits did I complete this week or quantos hábitos concluí esta semana, call get_period_analytics with metric completedHabits. "
                + "For how many times did I complete habits or quantas vezes concluí hábitos, use positiveLogRecords. "
                + "Habits are distinct positive recurring habits, including independently logged children; tasks and slips do not count as habits. "
                + "For a supported periodTotal or periodComparison block, write at most one short sentence in the person's language without figures, then the block. "
                + "Period blocks with metric completedHabits and unit habits also require supportsCompletedHabitsMetric, in addition to supportsParts and their advertised kind. "
                + $"Completed habits metric supported: {client?.SupportsCompletedHabitsMetric == true}. "
                + "For an unsupported metric, answer from the queried facts in text.");
        if (client?.SupportsParts == true && client.PartKinds is { Count: > 0 })
            prompt = string.Join(Environment.NewLine, prompt,
                "When a tool result carries readDirective, use that exact directive for its block. Different calls retain distinct scopes. "
                + "get_period_analytics returns periodTotal or periodComparison; emit it only when that kind is explicitly advertised. "
                + "get_habit_history uses recordList. New period kinds supported: "
                + string.Join(", ", client.PartKinds?.Where(k => k is "periodTotal" or "periodComparison") ?? []));
        prompt = string.Join(Environment.NewLine, prompt,
            "Dated habitList blocks require supportsDatedHabitListCard, including date-correct actions. "
            + "habitHistory recordList blocks require supportsHabitHistoryCard. Other card flags do not establish subtype support. "
            + $"Dated habit lists supported: {client?.SupportsDatedHabitListCard == true}. Habit history supported: {client?.SupportsHabitHistoryCard == true}. "
            + "For an unsupported subtype, answer from the queried facts in text.");
        if (UsesComposition(request, context))
            prompt = string.Join(Environment.NewLine, prompt,
                "The server composes the leading answer blocks from successful typed reads without framing prose. Call only the reads needed for the request. Additional blocks require explicitly requested distinct subjects. Put clarification in ask and every write in its existing approval preview. Do not repeat block facts in prose.",
                TerminalReadDirective.Instruction);
        else if (client?.SupportsParts == true)
            prompt = string.Join(Environment.NewLine, prompt,
                "Write the reply in reading order, placing each card directive on its own line where its block belongs. Use at most three directives and never repeat a block's figures in the text.");
        if (client?.SupportsHabitListCard == true)
            prompt = AppendCardPrompt(prompt, client, PartKinds.HabitList,
                HabitListCardBuilder.PromptInstruction, HabitListCardBuilder.PartsPromptInstruction);
        if (client?.SupportsGoalListCard == true && activeToolNames.Contains("review_goals"))
            prompt = AppendCardPrompt(prompt, client, PartKinds.GoalList,
                GoalListCardBuilder.PromptInstruction, GoalListCardBuilder.PartsPromptInstruction);

        if (activeToolNames.Contains("get_habit_ranking"))
            prompt = string.Join(Environment.NewLine, prompt, HabitRankingCardBuilder.PromptInstruction);

        prompt = AppendPeriodCardPrompts(prompt, client, context.EnabledFeatureFlags, activeToolNames);
        prompt = AppendStatusCardPrompts(prompt, client, context.EnabledFeatureFlags, activeToolNames);
        return AppendListAndAccountPrompts(prompt, client, context.EnabledFeatureFlags, activeToolNames);
    }

    private static string AppendPeriodCardPrompts(
        string prompt, AgentClientContext? client, IReadOnlyList<string> enabledFlags,
        IReadOnlyCollection<string> activeToolNames)
    {
        if (enabledFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled))
            return prompt;

        if (client?.SupportsMetricsCard == true)
        {
            prompt = AppendCardPrompt(prompt, client, PartKinds.Metrics,
                MetricsCardBuilder.PromptInstruction, MetricsCardBuilder.PartsPromptInstruction);
            if (activeToolNames.Contains("get_habit_metrics"))
                prompt = AppendCardPrompt(prompt, client, PartKinds.Metrics,
                    MetricsCardBuilder.HabitPromptInstruction, MetricsCardBuilder.HabitPartsPromptInstruction);
        }
        if (client?.SupportsPeriodInsightCard == true && activeToolNames.Contains("get_retrospective"))
            prompt = AppendCardPrompt(prompt, client, PartKinds.PeriodInsight,
                PeriodInsightCardBuilder.PromptInstruction, PeriodInsightCardBuilder.PartsPromptInstruction);

        return prompt;
    }

    private static string AppendStatusCardPrompts(
        string prompt, AgentClientContext? client, IReadOnlyList<string> enabledFlags,
        IReadOnlyCollection<string> activeToolNames)
    {
        if (enabledFlags.Contains(FeatureFlagKeys.AstraStatusBlocksDisabled))
            return prompt;

        if (client?.SupportsDaySummaryCard == true && activeToolNames.Contains("get_daily_summary"))
            prompt = AppendCardPrompt(prompt, client, PartKinds.DaySummary,
                StatusCardBuilder.DayPrompt, StatusCardBuilder.DayPartsPrompt);
        if (client?.SupportsStreakCard == true && activeToolNames.Contains("get_gamification_overview"))
            prompt = AppendCardPrompt(prompt, client, PartKinds.Streak,
                StatusCardBuilder.StreakPrompt, StatusCardBuilder.StreakPartsPrompt);
        if (client?.SupportsCalendarCard == true && activeToolNames.Contains("get_calendar_overview"))
            prompt = AppendCardPrompt(prompt, client, PartKinds.Calendar,
                StatusCardBuilder.CalendarPrompt, StatusCardBuilder.CalendarPartsPrompt);

        return prompt;
    }

    private static string AppendListAndAccountPrompts(
        string prompt, AgentClientContext? client, IReadOnlyList<string> enabledFlags,
        IReadOnlyCollection<string> activeToolNames)
    {
        if (client?.SupportsRecordListCard == true
            && !enabledFlags.Contains(FeatureFlagKeys.AstraRecordListsDisabled)
            && activeToolNames.Any(name => name is "get_notifications" or "list_tags" or "get_checklist_templates" or "get_api_keys"))
        {
            prompt = AppendCardPrompt(prompt, client, PartKinds.RecordList,
                RecordListCardBuilder.PromptInstruction, RecordListCardBuilder.PartsPromptInstruction);
        }

        if (client?.SupportsAccountRowsCard == true
            && !enabledFlags.Contains(FeatureFlagKeys.AstraAccountRowsDisabled)
            && activeToolNames.Any(name => name is "get_profile" or "get_subscription_overview" or "get_referral_overview"))
        {
            prompt = AppendCardPrompt(prompt, client, PartKinds.AccountRows,
                AccountRowsCardBuilder.PromptInstruction, AccountRowsCardBuilder.PartsPromptInstruction);
        }

        return prompt;
    }

    private static string AppendCardPrompt(
        string prompt, AgentClientContext? client, string kind, string legacyInstruction, string partsInstruction)
    {
        if (client?.SupportsParts != true)
            return string.Join(Environment.NewLine, prompt, legacyInstruction);

        return client.PartKinds?.Contains(kind, StringComparer.Ordinal) == true
            ? string.Join(Environment.NewLine, prompt, partsInstruction)
            : prompt;
    }

    private sealed record AgentSnapshotInputs(
        IReadOnlyList<string> FeatureFlags,
        IReadOnlyCollection<Tag> UserTags,
        IReadOnlyCollection<string> ChecklistTemplateNames,
        IReadOnlyCollection<Habit> ActiveHabits,
        IReadOnlyCollection<Goal> ActiveGoals);

    private static AgentContextSnapshot BuildAgentContextSnapshot(
        User? user,
        AgentClientContext? clientContext,
        AgentSnapshotInputs inputs,
        bool hasProAccess)
    {
        var (featureFlags, userTags, checklistTemplateNames, activeHabits, activeGoals) = inputs;
        return new AgentContextSnapshot(
            hasProAccess ? "pro" : "free",
            ChatLocale.Resolve(clientContext, user?.Language),
            user?.TimeZone,
            hasProAccess && (user?.AiSummaryEnabled ?? true),
            user?.WeekStartDay ?? 1,
            user?.ThemePreference,
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
            checklistTemplateNames
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            activeHabits
                .OrderByDescending(habit => habit.UpdatedAtUtc)
                .Select(habit => habit.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            activeGoals
                .OrderByDescending(goal => goal.UpdatedAtUtc)
                .Select(goal => goal.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            ClientContext: clientContext,
            Uses24HourClock: TimeFormatResolver.Uses24HourClock(user?.Uses24HourClockPreference, user?.TimeZone));
    }

    private static ResponseDirectiveStreamFilter? BuildAiStreamFilter(Func<ChatStreamEvent, Task>? streamSink)
    {
        if (streamSink is null)
            return null;

        return new ResponseDirectiveStreamFilter(streamSink);
    }

    private sealed class ResponseDirectiveStreamFilter(Func<ChatStreamEvent, Task> streamSink)
    {
        private ResponseDirectiveParser _parser = new();

        public async Task HandleAsync(AiStreamEvent aiEvent)
        {
            if (aiEvent.Kind == AiStreamEventKind.Reset)
            {
                _parser = new ResponseDirectiveParser();
                await streamSink(ChatStreamEvent.Reset());
                return;
            }

            await EmitAsync(_parser.Process(aiEvent.Text));
        }

        public Task FlushAsync() => EmitAsync(_parser.Process(null, flush: true));

        private async Task EmitAsync(string text)
        {
            if (text.Length > 0)
                await streamSink(ChatStreamEvent.Delta(text));
        }
    }

    /// <summary>
    /// Strips a JSON wrapper from the AI response text, extracting the "aiMessage" property
    /// if the model returned a raw JSON object instead of using function calling.
    /// </summary>
    private static string? StripJsonWrapper(string? text)
    {
        if (text is null || !text.TrimStart().StartsWith('{'))
            return text;

        if (!TryParseJsonObject(text, out var document))
            return text;

        using (document)
        {
            return document.RootElement.TryGetProperty("aiMessage", out var messageElement)
                ? messageElement.GetString()
                : text;
        }
    }

    private static bool TryParseJsonObject(string text, [NotNullWhen(true)] out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }
}
