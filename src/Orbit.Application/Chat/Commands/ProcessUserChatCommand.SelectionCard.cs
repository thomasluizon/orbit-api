using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private const string RecordQuestionId = "records";

    private static bool IsRecordSelectionAsk(JsonElement args) =>
        JsonArgumentParser.GetOptionalString(args, "missing_input") is "record_selection" or "bulk_selection";

    /// <summary>
    /// An option id in the form of a record id claims a record. A record selection names only records that a read
    /// of this turn returned, so an id that no read returned is a guess and the card is refused.
    /// </summary>
    private static string? UnknownRecordOptionError(JsonElement args, AiBlockDefinition definition, ToolExecutionAccumulator results)
    {
        if (!IsRecordSelectionAsk(args))
            return null;
        var ids = definition.Form == AiBlockKinds.Question
            ? (definition.Questions ?? []).SelectMany(question => question.Options ?? []).Select(option => option.Id)
            : definition.Options.Select(option => option.Id);
        var unknown = ids.Count(id => Guid.TryParse(id, out _) && !results.ReadHabitItems.ContainsKey(id));
        return unknown == 0 ? null
            : $"{unknown} option ID(s) name no habit that query_habits returned in this turn. Copy each habit ID exactly from that read result, or give the option an ID that names no record. The card was not shown.";
    }

    private sealed record SelectionCardVerdict(AiBlockDefinition? Definition, string? Error);

    /// <summary>
    /// A card about which records of a same-titled group the request means must hold every answer: each record is
    /// selectable, and the card takes several selections when the request names several. The guard owns the
    /// candidates, so the server completes a short card from them (a choice up to 4, one question up to 10) and
    /// refuses the card when the client can show neither.
    /// </summary>
    private static SelectionCardVerdict HoldTheAnswer(JsonElement args, AiBlockDefinition definition,
        ProcessUserChatCommand request, ToolExecutionAccumulator results, string? language)
    {
        var keep = new SelectionCardVerdict(definition, null);
        if (!IsRecordSelectionAsk(args) || definition.DraftClarification is not null
            || RecordCardOf(definition) is not { } card)
            return keep;
        var named = card.OptionTargets.SelectMany(targets => targets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (SelectionGroup(named, results) is not { } group)
            return keep;
        var several = card.PicksSeveral || HabitSetRequestScope.NamesSeveralMembers(request.Message, group[0].Title);
        if (group.All(candidate => named.Contains(candidate.Id)) && card.OptionTargets.All(targets => targets.Length > 0))
            return card.OptionTargets.All(targets => targets.Length == 1) && several && !card.PicksSeveral
                ? new(TakeSeveral(definition), null) : keep;
        return CompleteRecordCard(card, group, several, request.ClientContext?.PartKinds ?? [], LocaleHelper.IsPortuguese(language));
    }

    /// <summary>
    /// A choice, or a question form with one choice question: its prompt, whether it takes several selections, its
    /// free-text setting, and the records each option stands for.
    /// </summary>
    private sealed record RecordCard(string Prompt, bool PicksSeveral, bool AllowFreeText, string[][] OptionTargets);

    private static RecordCard? RecordCardOf(AiBlockDefinition definition) => definition switch
    {
        { Form: AiBlockKinds.Choice } => new(definition.Title, definition.MultiSelect, definition.AllowFreeText,
            [.. definition.Options.Select(option => AiChoiceSelection.TargetsOf(option).ToArray())]),
        { Form: AiBlockKinds.Question, Questions: [{ Type: "singleChoice" or "multipleChoice", Options: { } options } question] } =>
            new(question.Prompt, question.Type == "multipleChoice", question.AllowFreeText,
                [.. options.Select(option => Guid.TryParse(option.Id, out _) ? new[] { option.Id } : [])]),
        _ => null
    };

    private static SelectionCardVerdict CompleteRecordCard(RecordCard card, IReadOnlyList<HabitListCardItem> group,
        bool several, IReadOnlyCollection<string> kinds, bool portuguese)
    {
        string Position(int index) => string.Format(CultureInfo.InvariantCulture, portuguese ? "{0}º de {1}" : "{0} of {1}",
            index + 1, group.Count);
        if (group.Count <= 4 && kinds.Contains(AiBlockKinds.Choice, StringComparer.Ordinal) && card.Prompt.Length <= 120)
            return new(new AiBlockDefinition(AiBlockKinds.Choice, card.Prompt,
                [.. group.Select((candidate, index) => new AiBlockOption(candidate.Id, Shorten(candidate.Title, 20), Position(index)))],
                [], several, card.AllowFreeText), null);
        if (group.Count <= 10 && kinds.Contains(AiBlockKinds.Question, StringComparer.Ordinal))
            return new(new AiBlockDefinition(AiBlockKinds.Question, string.Empty, [], [],
                Questions: [new AiQuestion(RecordQuestionId, several ? "multipleChoice" : "singleChoice", card.Prompt,
                    [.. group.Select((candidate, index) => new AiQuestionOption(candidate.Id,
                        $"{Shorten(candidate.Title, 100)} ({Position(index)})"))], card.AllowFreeText)]), null);
        return new(null, $"This card cannot hold the answer: the person must be able to select each of the {group.Count} habits titled \"{group[0].Title}\", and this client cannot show one option for each of them. Use form choice with 2 to 4 options whose target_ids together hold every habit ID that query_habits returned for that title, and set multiSelect=true when the person picks several options. The card was not shown.");
    }

    /// <summary>
    /// The same-titled group that a record card is about: the one group that the options name, or, when the options
    /// name no record, the one same-titled group of the turn's selections. Candidates keep the order of the read.
    /// </summary>
    private static IReadOnlyList<HabitListCardItem>? SelectionGroup(IReadOnlySet<string> named, ToolExecutionAccumulator results)
    {
        var selected = new[] { results.PendingSelection }.Concat(results.SelectionCandidates).OfType<HabitSelectionCandidates>()
            .SelectMany(selection => selection.Candidates);
        if (named.Count == 0)
            return SameTitleGroups(selected) is [var only] ? only : null;
        var touched = SameTitleGroups(selected.Concat(results.ReadHabitItems.Values))
            .Where(group => group.Any(candidate => named.Contains(candidate.Id))).ToArray();
        return touched is [var group] ? group : null;
    }

    private static HabitListCardItem[][] SameTitleGroups(IEnumerable<HabitListCardItem> candidates) => [.. candidates
        .DistinctBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
        .GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title)).Where(group => group.Count() > 1)
        .Select(group => group.ToArray())];

    private static AiBlockDefinition TakeSeveral(AiBlockDefinition definition) => definition.Form == AiBlockKinds.Question
        ? definition with
        {
            Questions = [definition.Questions![0] with
            {
                Type = "multipleChoice",
                Options = [.. definition.Questions[0].Options!.Select(option => option with { Recommended = false })]
            }]
        }
        : definition with { MultiSelect = true };

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// After an answered choice that splits records into a chosen and an unchosen set, a write of the turn was
    /// refused and the prepared changes touch only one set. The request had an action for each set, so the other
    /// set still needs its change. Returns the records of that set, or null.
    /// </summary>
    private IReadOnlyList<string>? UnpreparedAnsweredSet(ToolExecutionAccumulator results)
    {
        if (results.AnsweredChoice is not { ChosenTargets.Count: > 0, UnchosenTargets.Count: > 0 } choice
            || !results.OperationResults.Any(operation => operation.Status is AgentOperationStatus.Denied or AgentOperationStatus.Failed
                && ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }))
            return null;
        var prepared = results.PendingOperations
            .SelectMany(operation => (operation.Items ?? []).Select(item => item.EntityId)
                .Concat((operation.Changes ?? []).Select(change => (Guid?)change.EntityId)))
            .OfType<Guid>().Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = choice.ChosenTargets.Any(prepared.Contains);
        var unchosen = choice.UnchosenTargets.Any(prepared.Contains);
        return chosen == unchosen ? null : chosen ? choice.UnchosenTargets : choice.ChosenTargets;
    }

    /// <summary>
    /// Tells the model, on each write of the round that it prepared, which answered records still have no change,
    /// so the remaining action is prepared in this turn and the preview holds the whole request.
    /// </summary>
    private void NoteUnpreparedAnsweredSet(List<AiToolCallResult> toolResults,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ToolExecutionAccumulator results)
    {
        if (UnpreparedAnsweredSet(results) is not { } remaining)
            return;
        for (var index = 0; index < toolResults.Count; index++)
            if (outcomes.GetValueOrDefault(toolResults[index].Id)?.PendingOperation is not null)
                toolResults[index] = toolResults[index] with
                {
                    Payload = new
                    {
                        preview = toolResults[index].Payload,
                        remaining_answered_records = remaining,
                        instruction = "This approval preview covers only one of the two sets of the answered selection, and a write in this turn was refused. Prepare the requested change for remaining_answered_records now, with those exact IDs, so the preview holds the whole request."
                    }
                };
    }
}
