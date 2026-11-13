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
        return CompleteRecordCard(card, OfferedRecords(named, group, results), group, several,
            request.ClientContext?.PartKinds ?? [], LocaleHelper.IsPortuguese(language));
    }

    /// <summary>
    /// The records that a completed card offers, in the order of the read: every record that the card offered and
    /// each member of the group, or, when the options name no record, every candidate of the selections that hold
    /// the group.
    /// </summary>
    private static HabitListCardItem[] OfferedRecords(IReadOnlySet<string> named, HabitListCardItem[] group,
        ToolExecutionAccumulator results)
    {
        var members = group.Select(candidate => candidate.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offered = named.Count > 0
            ? named.Concat(members).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : Selections(results).Where(selection => selection.Candidates.Any(candidate => members.Contains(candidate.Id)))
                .SelectMany(selection => selection.Candidates).Select(candidate => candidate.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. TurnRecords(results).Where(record => offered.Contains(record.Id))];
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

    private static SelectionCardVerdict CompleteRecordCard(RecordCard card, HabitListCardItem[] offered,
        HabitListCardItem[] group, bool several, IReadOnlyCollection<string> kinds, bool portuguese)
    {
        if (offered.Length <= 4 && kinds.Contains(AiBlockKinds.Choice, StringComparer.Ordinal) && card.Prompt.Length <= 120)
            return new(new AiBlockDefinition(AiBlockKinds.Choice, card.Prompt,
                [.. offered.Select(record => new AiBlockOption(record.Id, Shorten(record.Title, 20), PlaceInGroup(group, record.Id, portuguese)))],
                [], several, card.AllowFreeText), null);
        if (offered.Length <= 10 && kinds.Contains(AiBlockKinds.Question, StringComparer.Ordinal))
            return new(new AiBlockDefinition(AiBlockKinds.Question, string.Empty, [], [],
                Questions: [new AiQuestion(RecordQuestionId, several ? "multipleChoice" : "singleChoice", card.Prompt,
                    [.. offered.Select(record => new AiQuestionOption(record.Id, PlaceInGroup(group, record.Id, portuguese) is { } place
                        ? $"{Shorten(record.Title, 100)} ({place})" : Shorten(record.Title, 120)))], card.AllowFreeText)]), null);
        return new(null, $"This card cannot hold the answer: the person must be able to select each of the {offered.Length} habits that the card is about, {group.Length} of them titled \"{group[0].Title}\", and this client cannot show one option for each of them. Use form choice with 2 to 4 options whose target_ids together hold each of those habit IDs, and set multiSelect=true when the person picks several options. The card was not shown.");
    }

    /// <summary>
    /// The place of a record among the records that share its title, in the order the read returned them: "2 of 6",
    /// or "2º de 6" in Portuguese. Null when the record is not a member of the group.
    /// </summary>
    private static string? PlaceInGroup(HabitListCardItem[] group, string id, bool portuguese)
    {
        var index = Array.FindIndex(group, member => string.Equals(member.Id, id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null
            : string.Format(CultureInfo.InvariantCulture, portuguese ? "{0}º de {1}" : "{0} of {1}", index + 1, group.Length);
    }

    /// <summary>
    /// The same-titled group that a record card is about: the one group that the options name, or, when the options
    /// name no record, the one same-titled group of the turn's selections. Candidates keep the order of the read.
    /// </summary>
    private static HabitListCardItem[]? SelectionGroup(IReadOnlySet<string> named, ToolExecutionAccumulator results)
    {
        var selected = Selections(results).SelectMany(selection => selection.Candidates)
            .Select(candidate => candidate.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = SameTitleGroups(TurnRecords(results).Where(record => named.Count > 0 || selected.Contains(record.Id)))
            .Where(group => named.Count == 0 || group.Any(candidate => named.Contains(candidate.Id))).ToArray();
        return groups is [var group] ? group : null;
    }

    private static IEnumerable<HabitSelectionCandidates> Selections(ToolExecutionAccumulator results) =>
        new[] { results.PendingSelection }.Concat(results.SelectionCandidates).OfType<HabitSelectionCandidates>();

    /// <summary>
    /// Every record that a read of the turn returned, in the order the reads returned them, then each selection
    /// candidate that no read returned.
    /// </summary>
    private static IEnumerable<HabitListCardItem> TurnRecords(ToolExecutionAccumulator results) => results.ReadHabitItems.Values
        .Concat(Selections(results).SelectMany(selection => selection.Candidates))
        .DistinctBy(record => record.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The records of the turn that share the title, in the order the reads returned them.
    /// </summary>
    private static HabitListCardItem[] SameTitleRecords(string normalizedTitle, ToolExecutionAccumulator results) =>
        [.. TurnRecords(results).Where(record => HabitNameMatcher.Normalize(record.Title) == normalizedTitle)];

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
}
