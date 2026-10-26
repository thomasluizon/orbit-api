using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ResponseCards WithDaySummaryIntro(ResponseCards cards, string? language, ChatContext context,
        AiResponse response, ToolExecutionAccumulator results) =>
        response.IsTruncated || results.HasBudgetRecovery || HasPendingWritePreview(results)
            || results.OperationResults.Any(operation => operation.Status != AgentOperationStatus.Succeeded)
            ? cards : WithDaySummaryIntro(cards, language, context.UserToday);

    private static ResponseCards WithDaySummaryIntro(ResponseCards cards, string? language, DateOnly today)
    {
        var blocks = cards.Parts?.OfType<ChatBlockPart>().ToList();
        var day = blocks is { Count: 1 } && blocks[0].Block is DaySummaryCard block
            ? block : cards.Parts is null ? cards.DaySummary : null;
        if (day is null || day.Date != today || day.DateTo is { } to && to != day.Date)
            return cards;
        var intro = LocaleHelper.IsPortuguese(language)
            ? "Aqui está o que você concluiu e o que falta para hoje."
            : "Here is what you finished and what is left for today.";
        return cards with
        {
            AiMessage = intro,
            Parts = blocks is { Count: 1 } ? [new ChatTextPart(intro), blocks[0]] : cards.Parts,
            ServerComposedText = true
        };
    }
}
