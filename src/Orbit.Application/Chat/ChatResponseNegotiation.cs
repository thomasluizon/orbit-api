using System.Text.Json;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class ChatResponseNegotiation
{
    public static async Task<ChatResponse> RecoverEmptyAnswerAsync(ProcessUserChatCommand request,
        string? language, ChatResponse? response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = LocaleHelper.IsPortuguese(language)
            ? "Não consegui preparar uma resposta. Tente novamente."
            : "I couldn't prepare an answer. Please try again.";
        if (request.StreamSink is not null)
        {
            await request.StreamSink(ChatStreamEvent.Reset());
            await request.StreamSink(ChatStreamEvent.Delta(message));
        }
        return (response ?? new ChatResponse(null, [], CorrelationId: request.CorrelationId)) with
        {
            AiMessage = message,
            FollowUps = null,
            Parts = request.ClientContext?.SupportsParts == true ? [new ChatTextPart(message)] : null
        };
    }

    public static ChatResponse ForAllowance(ChatResponse response, AgentClientContext? client, bool consumed)
    {
        bool? allowance = client?.SupportsMessageAllowance == true ? consumed : null;
        return response.MessageAllowanceConsumed == allowance ? response : response with { MessageAllowanceConsumed = allowance };
    }

    public static ChatResponse ForPendingPreview(ChatResponse response, bool supportsParts, string? language = null)
    {
        if (supportsParts || response.PendingOperations is not { Count: > 0 } previews
            || !string.IsNullOrWhiteSpace(response.AiMessage)) return response;
        var names = previews.SelectMany(preview => preview.Items?.Select(item => item.EntityName)
                ?? preview.Changes?.Select(change => change.EntityName) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .Select(name => "- " + name.ReplaceLineEndings(" "));
        var instruction = LocaleHelper.IsPortuguese(language)
            ? "Revise as alterações propostas antes de aprovar."
            : "Review the proposed changes before approving.";
        return response with { AiMessage = string.Join("\n", new[] { instruction }.Concat(names)), Parts = null };
    }

    public static ChatResponse ForClient(ChatResponse response, AgentClientContext? client, IReadOnlyList<string> flags,
        string? language = null, string? timeZone = null)
    {
        response = ForAllowance(response, client, response.MessageAllowanceConsumed ?? false);
        var supportsParts = client?.SupportsParts == true && !flags.Contains(FeatureFlagKeys.AstraPartsDisabled);
        if (response.Parts is null) return ForPendingPreview(response, supportsParts, language);
        var negotiated = new List<ChatResponsePart>();
        var downgraded = false;
        var readFallbacks = new List<ChatTextPart>();
        foreach (var part in response.Parts)
        {
            if (part is not ChatBlockPart block)
            {
                negotiated.Add(part);
                continue;
            }
            var value = Decode(block);
            if (supportsParts && Supported(block.Kind, value, client, flags))
                negotiated.Add(block);
            else
            {
                downgraded = true;
                var fallback = new ChatTextPart(Fallback(value, language, timeZone));
                negotiated.Add(fallback);
                if (value is ReadBlockCard or HabitMonthCard) readFallbacks.Add(fallback);
            }
        }
        if (readFallbacks.Count > 0 && response.Actions.Count == 0 && response.PendingOperations is not { Count: > 0 })
            negotiated.RemoveAll(part => part is ChatTextPart text && !readFallbacks.Contains(text));
        var text = string.Join("\n\n", negotiated.OfType<ChatTextPart>().Select(part => part.Text));
        var followUps = client?.SupportsFollowUps == true && !flags.Contains(FeatureFlagKeys.AstraFollowUpsDisabled);
        return ForPendingPreview(response with
        {
            Parts = supportsParts ? negotiated : null,
            AiMessage = downgraded ? text : response.AiMessage,
            FollowUps = followUps ? response.FollowUps : null,
            FollowUpContext = followUps && client?.SupportsFollowUpContext == true ? response.FollowUpContext : null
        }, supportsParts, language);
    }

    private static bool Supported(string kind, object? value, AgentClientContext? client, IReadOnlyList<string> flags) =>
        value is not null && client?.PartKinds?.Contains(kind, StringComparer.Ordinal) == true
        && ProcessUserChatCommandHandler.ReadSubtypeEnabled(value, client)
        && (kind switch
        {
            "choice" or "pickList" => !flags.Contains(FeatureFlagKeys.AstraAskBlocksDisabled),
            "habitRanking" => !flags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled),
            _ => ProcessUserChatCommandHandler.ReadKindEnabled(kind, client, flags)
        });

    private static object? Decode(ChatBlockPart part)
    {
        if (part.Block is not JsonElement element) return part.Block;
        Type? type = part.Kind switch
        {
            "habitList" => typeof(HabitListCard),
            "goalList" => typeof(GoalListCard),
            "metrics" => typeof(MetricsCard),
            "daySummary" => typeof(DaySummaryCard),
            "streak" => typeof(StreakCard),
            "calendar" => typeof(CalendarCard),
            "habitMonth" => typeof(HabitMonthCard),
            "habitSuggestions" => typeof(HabitSuggestionsCard),
            "recordList" => typeof(RecordListCard),
            "accountRows" => typeof(AccountRowsCard),
            "periodInsight" => typeof(PeriodInsightCard),
            "periodTotal" => typeof(PeriodTotalCard),
            "periodComparison" => typeof(PeriodComparisonCard),
            "habitRanking" => typeof(HabitRankingCard),
            "choice" => typeof(AstraChoiceBlock),
            "pickList" => typeof(AstraPickListBlock),
            _ => null
        };
        try
        {
            return type is null ? null : element.Deserialize(type, ChatStreamEvent.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Fallback(object? value, string? language, string? timeZone)
    {
        var text = value switch
        {
            HabitRankingCard card => HabitRankingCardBuilder.Readable(card.Ranking, language),
            HabitSuggestionsCard card => HabitSuggestionsPresentation.Readable(card),
            AstraChoiceBlock block => block.Question,
            AstraPickListBlock block => block.Title,
            null => null,
            _ => ProcessUserChatCommandHandler.ReadFallback(value, language, timeZone)
        };
        return string.IsNullOrWhiteSpace(text) ? ErrorCopy.Resolve("chat.read.clientUnavailable", LocaleHelper.IsPortuguese(language)) : text;
    }
}
