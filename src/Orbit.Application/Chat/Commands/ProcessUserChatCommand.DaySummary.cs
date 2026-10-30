using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ResponseCards WithDaySummaryIntro(ResponseCards cards, string? language, ChatContext context,
        AiResponse response, ToolExecutionAccumulator results, AgentClientContext? client) =>
        response.IsTruncated || results.HasBudgetRecovery || HasPendingWritePreview(results)
            || results.OperationResults.Any(operation => operation.Status != AgentOperationStatus.Succeeded)
            ? cards : WithDaySummaryIntro(cards, language, context.UserToday, results, client?.SupportsParts == true);

    private static ResponseCards WithDaySummaryIntro(ResponseCards cards, string? language, DateOnly today,
        ToolExecutionAccumulator results, bool supportsParts)
    {
        if (results.OperationResults.Any(operation => operation.OperationId != "get_daily_summary"))
            return cards;
        var blocks = cards.Parts?.OfType<ChatBlockPart>().ToList();
        var day = blocks is { Count: 1 } && blocks[0].Block is DaySummaryCard block
            ? block : cards.Parts is null ? cards.DaySummary : null;
        var operation = results.LastSuccessfulOperation("get_daily_summary");
        var narrative = operation is not null ? RequestedDaySummaryNarrative(operation) : null;
        var readableFallback = day is not null && !supportsParts || day is null && narrative is not null;
        if (readableFallback) day ??= operation?.ReadBlock?.Block as DaySummaryCard;
        if (day is null || day.Date != today || day.DateTo is { } to && to != day.Date)
            return cards;
        var intro = narrative ?? (LocaleHelper.IsPortuguese(language)
            ? "Aqui está o que você concluiu e o que falta para hoje."
            : "Here is what you finished and what is left for today.");
        return cards with
        {
            AiMessage = readableFallback ? string.Join("\n\n", new[] { intro, cards.AiMessage }
                .Where(text => !string.IsNullOrWhiteSpace(text))) : intro,
            Parts = blocks is { Count: 1 } ? [new ChatTextPart(intro), blocks[0]]
                : readableFallback && cards.Parts is not null ? [new ChatTextPart(intro), .. cards.Parts] : cards.Parts,
            ServerComposedText = true
        };
    }

    private static string? RequestedDaySummaryNarrative(AgentOperationResult operation)
    {
        if (operation.OperationId != "get_daily_summary" || operation.Status != AgentOperationStatus.Succeeded
            || operation.Payload is not DailySummaryResponse summary || string.IsNullOrWhiteSpace(summary.Summary)
            || operation.ReadBlock is not { Kind: "daySummary" } read)
            return null;
        return read.Read.Scope.TryGetProperty("generate_narrative", out var requested)
            && requested.ValueKind == JsonValueKind.True ? summary.Summary : null;
    }
}
