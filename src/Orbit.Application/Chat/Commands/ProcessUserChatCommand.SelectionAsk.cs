using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private const int MaxServerPickListItems = 10;

    private async Task<bool> EndsWithAskAsync(AiResponse response, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken ct) =>
        results.AskBlock is not null || await TryIssueSelectionPickListAsync(response, calls, outcomes, request, context, results, ct);

    /// <summary>
    /// A round that holds only <c>query_habits(for_selection=true)</c> for a log or skip selection already has
    /// every pickList input. The server issues that pickList through the ask tool, so the turn needs no second
    /// model round. Each item uses the date of its read; an undated read is for the user's today. Reads on
    /// different dates, or a pickList that the ask tool does not accept, leave the turn to the model.
    /// </summary>
    private async Task<bool> TryIssueSelectionPickListAsync(AiResponse response, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (request.BlockAnswer is not null || !AskEnabled(request, context)
            || request.ClientContext!.PartKinds?.Contains(AstraBlockKinds.PickList, StringComparer.Ordinal) != true
            || calls.Count == 0 || calls.Count != response.ToolCalls!.Count
            || !calls.All(IsSelectionRead) || SelectionAction(request.Message) is not { } action)
            return false;
        var cards = calls.Select(call => outcomes.GetValueOrDefault(call.Id)?.OperationResult is
        { Status: AgentOperationStatus.Succeeded, Payload: HabitListCard card } ? card : null).ToArray();
        if (cards.Any(card => card is null)) return false;
        var candidates = cards.SelectMany(card => card!.Items)
            .Where(item => !item.IsBadHabit && item.IsSkippedOnDate != true).DistinctBy(item => item.Id).ToArray();
        if (candidates.Length is 0 or > MaxServerPickListItems) return false;
        var dates = cards.Select(card => card!.Date ?? context.UserToday).Distinct().ToArray();
        if (dates.Length != 1) return false;
        var date = dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ask = new AiToolCall("ask", "server-selection-" + calls[0].Id, JsonSerializer.SerializeToElement(new
        {
            input = new
            {
                form = AstraBlockKinds.PickList,
                missing_input = "bulk_selection",
                title = LocaleHelper.IsPortuguese(context.Language) ? "Escolha os hábitos" : "Choose habits",
                action,
                allowFreeText = false,
                items = candidates.Select(item => new { id = item.Id, label = item.Title, arguments = new { habit_id = item.Id, date } })
            }
        }));
        var canRetryAsk = results.CanRetryAsk;
        var outcome = await ExecuteAskCallAsync(ask, request, results, ct);
        if (results.AskBlock is null)
        {
            results.CanRetryAsk = canRetryAsk;
            return false;
        }
        results.EvalTrace?.Record(ask, outcome.ToolResult, AgentOperationStatus.Succeeded, omitData: false);
        if (results.Recording is { Enabled: true } recording)
        {
            recording.ToolResults.Add(outcome.ToolResult);
            recording.ToolCalls.Add(new ConversationToolCall(ask.Id, ask.Name, ask.Args.Clone(), true, null));
        }
        return true;
    }

    private static bool IsSelectionRead(AiToolCall call) => call.Name == "query_habits"
        && call.Args.TryGetProperty("for_selection", out var selection) && selection.ValueKind == JsonValueKind.True;

    private static string? SelectionAction(string message)
    {
        var normalized = HabitNameMatcher.Normalize(message);
        var log = LogSelection().IsMatch(normalized);
        var skip = SkipSelection().IsMatch(normalized);
        return log == skip ? null : log ? "log_habits" : "skip_habits";
    }

    [GeneratedRegex(@"\b(?:log|complete|finish|check\s+off|mark\s+(?:\w+\s+)?(?:as\s+)?done|concluir|completar|finalizar|registrar|marcar\s+(?:\w+\s+)?(?:como\s+)?feitos?)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex LogSelection();

    [GeneratedRegex(@"\b(?:skip|pular|pule)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SkipSelection();
}
