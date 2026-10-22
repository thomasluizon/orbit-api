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
        CancellationToken cancellationToken)
    {
        var promptRequest = new PromptBuildRequest(
            context.PromptHabits,
            HasImage: request.ImageData is not null,
            UserTags: context.UserTags,
            UserToday: context.UserToday,
            ActiveGoals: context.ActiveGoals,
            IsHabitIndexPartial: context.IsPromptHabitIndexPartial,
            TodayHabitIds: context.TodayFacts.TodayIds,
            OverdueHabitIds: context.TodayFacts.OverdueIds,
            DoneTodayHabitIds: context.TodayFacts.DoneTodayIds);
        var agentSnapshot = BuildAgentContextSnapshot(
            context.User,
            request.ClientContext,
            new AgentSnapshotInputs(
                context.EnabledFeatureFlags,
                context.UserTags,
                context.ChecklistTemplates,
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
            ai.CatalogService.BuildDynamicSupplement(agentSnapshot));

        var toolNames = ai.ToolRegistry.GetAll().Select(tool => tool.Name).ToArray();

        var askEnabled = request.AuthMethod == AgentAuthMethod.Jwt
            && request.ClientContext?.SupportsParts == true
            && request.ClientContext.PartKinds?.Any(kind => kind is AstraBlockKinds.Choice or AstraBlockKinds.PickList) == true
            && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraAskBlocksDisabled);
        if (!askEnabled && toolNames.Contains("ask"))
            dynamicContext = string.Join(Environment.NewLine, dynamicContext,
                "The client cannot display ask blocks on this turn. Ask clarification questions in prose and do not call ask.");
        if (askEnabled)
            dynamicContext = string.Join(Environment.NewLine, dynamicContext,
                "Use ask when one short answer decides the next step, or several suggested items can be acted on together. Never use ask for a yes/no question the write preview already asks. At most one ask block per reply. An ask call ends the tool round: put it last and make no further tool calls. Supported ask forms: "
                + string.Join(", ", request.ClientContext!.PartKinds!.Where(kind => kind is AstraBlockKinds.Choice or AstraBlockKinds.PickList)));

        dynamicContext = AppendCardPromptInstructions(dynamicContext, request, context, toolNames);
        if (request.ClientContext?.SupportsFollowUps == true)
            dynamicContext = string.Join(Environment.NewLine, dynamicContext,
                request.ClientContext.SupportsParts == true
                    ? "After the reply's text and blocks, end with [[orbit:followups]] followed by two or three short next questions, one per line. Do not add other text after them."
                    : "After any card directive, end your reply with [[orbit:followups]] followed by two or three short next questions, one per line. Do not add other text after them.");

        var toolDeclarations = ai.ToolRegistry.GetAll()
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => (object)new { name = tool.Name, description = tool.Description, parameters = tool.GetParameterSchema() })
            .ToList();

        LogCallingAiIntentService(logger, toolDeclarations.Count);

        var reservation = request.BlockAnswer is not null ? Result.Success() : await execution.PayGateService.TryConsumeAiMessage(
            request.UserId,
            execution.UnitOfWork,
            cancellationToken);
        if (reservation.IsFailure)
            return reservation.PropagateError<AiResponse>();

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

    private static string AppendCardPromptInstructions(
        string prompt, ProcessUserChatCommand request, ChatContext context,
        IReadOnlyCollection<string> activeToolNames)
    {
        var client = request.ClientContext;
        if (client?.SupportsParts == true)
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
        IReadOnlyCollection<ChecklistTemplate> ChecklistTemplates,
        IReadOnlyCollection<Habit> ActiveHabits,
        IReadOnlyCollection<Goal> ActiveGoals);

    private static AgentContextSnapshot BuildAgentContextSnapshot(
        User? user,
        AgentClientContext? clientContext,
        AgentSnapshotInputs inputs,
        bool hasProAccess)
    {
        var (featureFlags, userTags, checklistTemplates, activeHabits, activeGoals) = inputs;
        return new AgentContextSnapshot(
            hasProAccess ? "pro" : "free",
            user?.Language,
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
