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

        if (request.ClientContext?.SupportsHabitListCard == true)
            systemPrompt = string.Join(Environment.NewLine, systemPrompt, HabitListCardBuilder.PromptInstruction);

        if (request.ClientContext?.SupportsGoalListCard == true)
            systemPrompt = string.Join(Environment.NewLine, systemPrompt, GoalListCardBuilder.PromptInstruction);

        if (request.ClientContext?.SupportsMetricsCard == true)
            systemPrompt = string.Join(Environment.NewLine, systemPrompt, MetricsCardBuilder.PromptInstruction);

        var activeToolNames = ChatToolGroups.ResolveActiveToolNames(
            ai.ToolRegistry.GetAll().Select(t => t.Name),
            BuildConversationText(request));

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

    private static string BuildConversationText(ProcessUserChatCommand request)
    {
        if (request.History is not { Count: > 0 })
            return request.Message;

        return request.Message + " " + string.Join(" ", request.History.Select(message => message.Content));
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

    private static MetricsDirectiveStreamFilter? BuildAiStreamFilter(Func<ChatStreamEvent, Task>? streamSink)
    {
        if (streamSink is null)
            return null;

        return new MetricsDirectiveStreamFilter(streamSink);
    }

    private sealed class MetricsDirectiveStreamFilter(Func<ChatStreamEvent, Task> streamSink)
    {
        private string _pending = string.Empty;

        public async Task HandleAsync(AiStreamEvent aiEvent)
        {
            if (aiEvent.Kind == AiStreamEventKind.Reset)
            {
                _pending = string.Empty;
                await streamSink(ChatStreamEvent.Reset());
                return;
            }

            _pending += aiEvent.Text ?? string.Empty;
            await DrainAsync(flush: false);
        }

        public Task FlushAsync() => DrainAsync(flush: true);

        private async Task DrainAsync(bool flush)
        {
            while (_pending.Length > 0)
            {
                var directiveIndex = _pending.IndexOf(MetricsCardBuilder.Directive, StringComparison.OrdinalIgnoreCase);
                if (directiveIndex >= 0)
                {
                    await EmitAsync(_pending[..directiveIndex]);
                    _pending = _pending[(directiveIndex + MetricsCardBuilder.Directive.Length)..];
                    continue;
                }

                var retainedCharacters = flush ? 0 : DirectivePrefixSuffixLength(_pending);
                var emittedLength = _pending.Length - retainedCharacters;
                if (emittedLength == 0)
                    return;

                await EmitAsync(_pending[..emittedLength]);
                _pending = _pending[emittedLength..];
            }
        }

        private async Task EmitAsync(string text)
        {
            if (text.Length > 0)
                await streamSink(ChatStreamEvent.Delta(text));
        }

        private static int DirectivePrefixSuffixLength(string text)
        {
            var maximumLength = Math.Min(text.Length, MetricsCardBuilder.Directive.Length - 1);
            for (var length = maximumLength; length > 0; length--)
            {
                if (text.EndsWith(MetricsCardBuilder.Directive[..length], StringComparison.OrdinalIgnoreCase))
                    return length;
            }

            return 0;
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
