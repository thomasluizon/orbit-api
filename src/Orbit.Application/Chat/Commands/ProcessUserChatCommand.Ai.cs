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
        bool skipTools,
        CancellationToken cancellationToken)
    {
        var promptRequest = new PromptBuildRequest(
            context.PromptHabits, context.UserFacts,
            HasImage: request.ImageData is not null,
            UserTags: context.UserTags,
            UserToday: context.UserToday,
            ActiveGoals: context.ActiveGoals,
            IsHabitIndexPartial: context.IsPromptHabitIndexPartial);
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
            ai.CatalogService.BuildStaticSupplement(),
            ai.PromptBuilder.BuildDynamic(promptRequest),
            ai.CatalogService.BuildDynamicSupplement(agentSnapshot));

        var activeToolNames = ChatToolGroups.ResolveActiveToolNames(
            ai.ToolRegistry.GetAll().Select(t => t.Name),
            BuildConversationText(request),
            GetEntryPointIntent(request));

        systemPrompt = AppendCardPromptInstructions(systemPrompt, request, context, activeToolNames);
        if (request.ClientContext?.SupportsFollowUps == true)
            systemPrompt = string.Join(Environment.NewLine, systemPrompt,
                "After any card directive, end your reply with [[orbit:followups]] followed by two or three short next questions, one per line. Do not add other text after them.");

        var toolDeclarations = skipTools
            ? new List<object>()
            : ai.ToolRegistry.GetAll()
                .Where(t => activeToolNames.Contains(t.Name))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => (object)new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.GetParameterSchema()
                })
                .ToList();

        LogCallingAiIntentService(logger, toolDeclarations.Count);

        var reservation = await execution.PayGateService.TryConsumeAiMessage(
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
                request.History),
            aiStreamSink,
            cancellationToken);
    }

    private static string AppendCardPromptInstructions(
        string prompt, ProcessUserChatCommand request, ChatContext context,
        IReadOnlyCollection<string> activeToolNames)
    {
        var client = request.ClientContext;
        if (client?.SupportsHabitListCard == true)
            prompt = string.Join(Environment.NewLine, prompt, HabitListCardBuilder.PromptInstruction);
        if (client?.SupportsGoalListCard == true && activeToolNames.Contains("review_goals"))
            prompt = string.Join(Environment.NewLine, prompt, GoalListCardBuilder.PromptInstruction);

        if (!context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled))
        {
            if (client?.SupportsMetricsCard == true)
            {
                prompt = string.Join(Environment.NewLine, prompt, MetricsCardBuilder.PromptInstruction);
                if (activeToolNames.Contains("get_habit_metrics"))
                    prompt = string.Join(Environment.NewLine, prompt, MetricsCardBuilder.HabitPromptInstruction);
            }
            if (client?.SupportsPeriodInsightCard == true && activeToolNames.Contains("get_retrospective"))
                prompt = string.Join(Environment.NewLine, prompt, PeriodInsightCardBuilder.PromptInstruction);
        }

        if (!context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraStatusBlocksDisabled))
        {
            if (client?.SupportsDaySummaryCard == true && activeToolNames.Contains("get_daily_summary"))
                prompt = string.Join(Environment.NewLine, prompt, StatusCardBuilder.DayPrompt);
            if (client?.SupportsStreakCard == true && activeToolNames.Contains("get_gamification_overview"))
                prompt = string.Join(Environment.NewLine, prompt, StatusCardBuilder.StreakPrompt);
            if (client?.SupportsCalendarCard == true && activeToolNames.Contains("get_calendar_overview"))
                prompt = string.Join(Environment.NewLine, prompt, StatusCardBuilder.CalendarPrompt);
        }

        return AppendListAndAccountPrompts(prompt, client, context.EnabledFeatureFlags, activeToolNames);
    }

    private static string AppendListAndAccountPrompts(
        string prompt, AgentClientContext? client, IReadOnlyList<string> enabledFlags,
        IReadOnlyCollection<string> activeToolNames)
    {
        if (client?.SupportsRecordListCard == true
            && !enabledFlags.Contains(FeatureFlagKeys.AstraRecordListsDisabled)
            && activeToolNames.Any(name => name is "get_notifications" or "list_tags" or "get_checklist_templates" or "get_api_keys"))
        {
            prompt = string.Join(Environment.NewLine, prompt, RecordListCardBuilder.PromptInstruction);
        }

        if (client?.SupportsAccountRowsCard == true
            && !enabledFlags.Contains(FeatureFlagKeys.AstraAccountRowsDisabled)
            && activeToolNames.Any(name => name is "get_profile" or "get_subscription_overview" or "get_referral_overview"))
        {
            prompt = string.Join(Environment.NewLine, prompt, AccountRowsCardBuilder.PromptInstruction);
        }

        return prompt;
    }

    private static string BuildConversationText(ProcessUserChatCommand request)
    {
        if (request.History is not { Count: > 0 })
            return request.Message;

        return request.Message + " " + string.Join(" ", request.History.Select(message => message.Content));
    }

    private static string? GetEntryPointIntent(ProcessUserChatCommand request) =>
        request.ClientContext?.EntryPointIntent;

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
            hasProAccess && (user?.AiMemoryEnabled ?? true),
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
            ClientContext: clientContext);
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
