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
        var message = ErrorCopy.Resolve("chat.answer.empty", LocaleHelper.IsPortuguese(ChatLocale.Resolve(request.ClientContext, language)));
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
        var instruction = ErrorCopy.Resolve("chat.answer.pendingPreview", LocaleHelper.IsPortuguese(language));
        return response with { AiMessage = string.Join("\n", new[] { instruction }.Concat(names)), Parts = null };
    }

    public static ChatResponse ForClient(ChatResponse response, AgentClientContext? client, IReadOnlyList<string> flags,
        string? language = null, string? timeZone = null)
    {
        language = ChatLocale.Resolve(client, language);
        response = ForAllowance(response, client, response.MessageAllowanceConsumed ?? false);
        var supportsParts = client?.SupportsParts == true && !flags.Contains(FeatureFlagKeys.AstraPartsDisabled);
        if (response.Parts is null) return ForPendingPreview(response, supportsParts, language);
        var negotiated = new List<ChatResponsePart>();
        var downgraded = false;
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
            }
        }
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
        && ProcessUserChatCommandHandler.ReadSubtypeEnabled(value, client, flags.Contains(FeatureFlagKeys.AstraBlockFirst))
        && (kind switch
        {
            "choice" or "pickList" or "question" => !flags.Contains(FeatureFlagKeys.AstraAskBlocksDisabled),
            "oneSmallStep" => true,
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
            "oneSmallStep" => typeof(OneSmallStepCard),
            "recordList" => typeof(RecordListCard),
            "accountRows" => typeof(AccountRowsCard),
            "periodInsight" => typeof(PeriodInsightCard),
            "periodTotal" => typeof(PeriodTotalCard),
            "periodComparison" => typeof(PeriodComparisonCard),
            "habitRanking" => typeof(HabitRankingCard),
            "choice" => typeof(AstraChoiceBlock),
            "pickList" => typeof(AstraPickListBlock),
            "question" => typeof(AstraQuestionBlock),
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
            OneSmallStepCard card => OneSmallStepCard.Readable(card),
            AstraChoiceBlock block => block.Question,
            AstraPickListBlock block => block.Title,
            AstraQuestionBlock block => string.Join("\n\n", new[] { block.Title, block.Intro }
                .Where(text => !string.IsNullOrWhiteSpace(text)).Concat(block.Questions.Select(question =>
                    question.Prompt + (question.Options is { Count: > 0 } options
                        ? "\n" + string.Join("\n", options.Select(option => "- " + option.Label))
                        : question.Type == "scale" ? $"\n1: {question.LowLabel}; 5: {question.HighLabel}"
                        : question.Type == "slider" ? FormattableString.Invariant($"\n{question.Min}..{question.Max} ({question.Step} {question.Unit})") : string.Empty)))),
            null => null,
            _ => ProcessUserChatCommandHandler.ReadFallback(value, language, timeZone)
        };
        return string.IsNullOrWhiteSpace(text) ? ErrorCopy.Resolve("chat.read.clientUnavailable", LocaleHelper.IsPortuguese(language)) : text;
    }
}
