using MediatR;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Application.Proactive.Queries;

public sealed record ProactiveItemResponse(
    Guid Id, string Trigger, DateOnly LocalDate, string Text, string ActionLabel, string ActionPrompt,
    DateTime CreatedAtUtc, DateTime? ShownAtUtc, DateTime? PushedAtUtc, ProactiveFacts? Facts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatResponsePart>? Parts = null);

public sealed record GetCurrentProactiveQuery(Guid UserId, AgentClientContext? ClientContext = null,
    AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt, IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false) : IRequest<Result<ProactiveItemResponse?>>;

public sealed class GetCurrentProactiveQueryHandler(IProactiveEngine engine, IAgentOperationExecutor operations,
    IFeatureFlagService featureFlags)
    : IRequestHandler<GetCurrentProactiveQuery, Result<ProactiveItemResponse?>>
{
    public async Task<Result<ProactiveItemResponse?>> Handle(GetCurrentProactiveQuery request, CancellationToken cancellationToken)
    {
        var item = await engine.GetCurrentAsync(request.UserId, cancellationToken);
        if (item is null) return Result.Success<ProactiveItemResponse?>(null);
        var facts = JsonSerializer.Deserialize<ProactiveFacts>(item.FactsJson);
        var parts = await BuildPartsAsync(request, item, facts, cancellationToken);
        return Result.Success<ProactiveItemResponse?>(new ProactiveItemResponse(
            item.Id, item.Trigger.ToString(), item.LocalDate, item.Text, item.ActionLabel, item.ActionPrompt,
            item.CreatedAtUtc, item.ShownAtUtc, item.PushedAtUtc,
            facts, parts));
    }

    private async Task<IReadOnlyList<ChatResponsePart>?> BuildPartsAsync(GetCurrentProactiveQuery request,
        ProactiveItem item, ProactiveFacts? facts, CancellationToken ct)
    {
        if (request.ClientContext?.SupportsParts != true) return null;
        var flags = await featureFlags.GetEnabledKeysForUserAsync(request.UserId, ct);
        if (flags.Contains(FeatureFlagKeys.AstraPartsDisabled)) return null;
        var (kind, tool, arguments) = ReadFor(item, facts);
        if (!ProcessUserChatCommandHandler.ReadKindEnabled(kind, request.ClientContext, flags)) return null;
        var response = await operations.ExecuteAsync(new(request.UserId, tool, arguments,
            AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential), ct);
        if (response.Operation.Status != AgentOperationStatus.Succeeded || response.Operation.ReadBlock is not { } read
            || read.Kind != kind) return null;
        read = ProcessUserChatCommandHandler.ForClient(read, request.ClientContext, flags);
        var negotiated = ChatResponseNegotiation.ForClient(new ChatResponse(null, [],
            Parts: [new ChatBlockPart(read.Kind, read.Block)]), request.ClientContext, flags);
        return negotiated.Parts?.OfType<ChatBlockPart>().Any() == true ? negotiated.Parts : null;
    }

    private static (string Kind, string Tool, JsonElement Arguments) ReadFor(ProactiveItem item, ProactiveFacts? facts)
    {
        var day = item.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return item.Trigger switch
        {
            ProactiveTrigger.RestartAfterMiss => ("daySummary", "get_daily_summary", JsonSerializer.SerializeToElement(new
            {
                date_from = item.LocalDate.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date_to = item.LocalDate.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                generate_narrative = false
            })),
            ProactiveTrigger.EveningCheckin or ProactiveTrigger.MorningPlan => ("habitList", "query_habits",
                JsonSerializer.SerializeToElement(new
                {
                    date = day,
                    include_overdue = item.Trigger == ProactiveTrigger.EveningCheckin,
                    is_completed = false,
                    is_bad_habit = false,
                    is_general = false
                })),
            ProactiveTrigger.GoalBehind => ("goalList", "review_goals", JsonSerializer.SerializeToElement(new { as_of = day })),
            ProactiveTrigger.BusyCalendarDay => ("calendar", "get_calendar_overview", JsonSerializer.SerializeToElement(new
            {
                date_from = day,
                date_to = day,
                include_calendars = false,
                include_auto_sync_state = false,
                include_suggestions = false
            })),
            ProactiveTrigger.WeeklyReview => ("periodInsight", "get_retrospective", JsonSerializer.SerializeToElement(new
            {
                period = "week",
                date_from = (facts?.PeriodFrom ?? item.LocalDate.AddDays(-ProactiveRules.WeekDays))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date_to = (facts?.PeriodTo ?? item.LocalDate.AddDays(-1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                generate_narrative = false
            })),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
    }
}
