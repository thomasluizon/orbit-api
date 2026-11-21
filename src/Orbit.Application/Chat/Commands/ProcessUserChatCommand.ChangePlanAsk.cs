using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private const int MaxChoiceRecords = 4;
    private const int MaxQuestionRecords = 10;

    /// <summary>
    /// The <c>ask</c> of the <c>ai_change_plan</c> path. The server checks only that the client can show the form, that
    /// the form is correct and that each record of <c>about</c> is a record of the person. It never decides that a
    /// question is needed or permitted: no read is required, <c>missing_input</c> is a label, and a failure is a typed
    /// error with no instruction.
    /// </summary>
    private async Task<ToolCallOutcome> ExecuteChangePlanAskAsync(AiToolCall call, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, CancellationToken ct)
    {
        var flags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(request.UserId, ct);
        if (AskUnavailable(request, flags, results, out var client))
            return TypedAskError(call, "ask_unavailable");
        var kinds = client.PartKinds ?? [];
        var user = await data.UserRepository.GetByIdAsync(request.UserId, ct);
        AiBlockDefinition definition;
        if (AboutRecords(call.Args) is { } about)
        {
            var card = RecordsCard(call, about, request.UserId, results.AllHabits, kinds,
                LocaleHelper.IsPortuguese(ChatLocale.Resolve(request.ClientContext, user?.Language)));
            if (card.Error is { } error)
                return error;
            definition = card.Definition!;
        }
        else
        {
            if (!CanAttemptAsk(call.Args, client))
                return TypedAskError(call, "ask_form_unsupported");
            if (ai.ToolRegistry.GetTool("ask") is not { } tool)
                return TypedAskError(call, "ask_unavailable");
            var result = await tool.ExecuteAsync(call.Args, request.UserId, ct);
            if (!result.Success || result.Payload is not AiBlockDefinition parsed)
                return TypedAskError(call, "ask_invalid_arguments", new Dictionary<string, object?> { ["detail"] = result.Error });
            definition = AskBlockNegotiation.Definition(parsed, client, call.Args);
        }
        if (!kinds.Contains(definition.Form, StringComparer.Ordinal))
            return TypedAskError(call, "ask_form_unsupported");
        return await IssueAskBlockAsync(call, definition, request, results, user, ct);
    }

    private static ToolCallOutcome TypedAskError(AiToolCall call, string code, IReadOnlyDictionary<string, object?>? details = null) =>
        new(TypedToolError(call, code, details), null, null, null, null, FailureKey: code);

    /// <summary>The record IDs of a choice about records, or null for every other form.</summary>
    private static IReadOnlyList<string>? AboutRecords(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object
        && JsonArgumentParser.GetOptionalString(args, "form") == AiBlockKinds.Choice
        && args.TryGetProperty(AskTool.AboutArgument, out var about) && about.ValueKind == JsonValueKind.Array
            ? [.. about.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty)]
            : null;

    private sealed record RecordsCardVerdict(AiBlockDefinition? Definition, ToolCallOutcome? Error);

    /// <summary>
    /// The card of a choice about records. It holds one option for each record, in the order of the app list, with the
    /// record title, its parent and its place among the records that share its title. Up to 4 records use a choice; up
    /// to 10 use one question when the client shows the question form. More records return
    /// <c>too_many_candidates</c> with the count and the limit of this client.
    /// </summary>
    private static RecordsCardVerdict RecordsCard(AiToolCall call, IReadOnlyList<string> about, Guid userId,
        IReadOnlyList<Habit> habits, IReadOnlyCollection<string> kinds, bool portuguese)
    {
        var question = JsonArgumentParser.GetOptionalString(call.Args, "question");
        var ids = about.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(question) || question.Length > 120 || ids.Length < 2)
            return new(null, TypedAskError(call, "ask_invalid_arguments",
                new Dictionary<string, object?> { ["detail"] = "A choice about records needs a question of 1 to 120 characters and at least 2 distinct records." }));
        var owned = habits.Where(habit => habit.UserId == userId).ToDictionary(habit => habit.Id);
        var unknown = ids.Count(id => !Guid.TryParse(id, out var parsed) || !owned.ContainsKey(parsed));
        if (unknown > 0)
            return new(null, TypedAskError(call, "unknown_record", new Dictionary<string, object?> { ["count"] = unknown }));
        var choice = kinds.Contains(AiBlockKinds.Choice, StringComparer.Ordinal);
        var form = kinds.Contains(AiBlockKinds.Question, StringComparer.Ordinal);
        if (!choice && !form)
            return new(null, TypedAskError(call, "ask_form_unsupported"));
        var limit = form ? MaxQuestionRecords : MaxChoiceRecords;
        if (ids.Length > limit || ids.Length > MaxChoiceRecords && !form)
            return new(null, TypedAskError(call, "too_many_candidates",
                new Dictionary<string, object?> { ["count"] = ids.Length, ["limit"] = limit }));

        var listOrder = HabitAppListOrder.Order(owned.Values);
        var chosen = ids.Select(id => Guid.Parse(id)).ToHashSet();
        var records = listOrder.Where(habit => chosen.Contains(habit.Id)).ToArray();
        var several = JsonArgumentParser.GetOptionalBool(call.Args, AskTool.SeveralArgument) == true;
        var freeText = JsonArgumentParser.GetOptionalBool(call.Args, "allowFreeText") == true;
        if (choice && records.Length <= MaxChoiceRecords)
            return new(new AiBlockDefinition(AiBlockKinds.Choice, question,
                [.. records.Select(record => new AiBlockOption(record.Id.ToString(), Shorten(record.Title, 20),
                    RecordDetail(record, owned, portuguese, 24)))],
                [], several, freeText), null);
        return new(new AiBlockDefinition(AiBlockKinds.Question, string.Empty, [], [],
            Questions: [new AiQuestion(RecordQuestionId, several ? "multipleChoice" : "singleChoice", question,
                [.. records.Select(record => new AiQuestionOption(record.Id.ToString(),
                    RecordDetail(record, owned, portuguese, 37) is { } detail
                        ? $"{Shorten(record.Title, 80)} ({detail})" : Shorten(record.Title, 120)))], freeText)]), null);
    }

    /// <summary>
    /// The parent title of a record and its place among the records that share its title in the app list, for
    /// example "Water, 5 of 6" or "Water, 5º de 6", in at most <paramref name="max"/> characters. A long parent title is
    /// shortened first, so the place always shows. A record with a title of its own has no place.
    /// </summary>
    private static string? RecordDetail(Habit record, IReadOnlyDictionary<Guid, Habit> owned, bool portuguese, int max)
    {
        var parent = record.ParentHabitId is { } parentId && owned.TryGetValue(parentId, out var parentHabit)
            ? parentHabit.Title : null;
        var group = HabitAppListOrder.SameTitleGroup(record, owned.Values, HabitNameMatcher.TitleKey);
        var place = group.Count < 2 ? null : string.Format(CultureInfo.InvariantCulture,
            portuguese ? "{0}º de {1}" : "{0} of {1}", group.ToList().FindIndex(habit => habit.Id == record.Id) + 1, group.Count);
        return (parent, place) switch
        {
            (null, null) => null,
            (null, _) => place,
            (_, null) => Shorten(parent, max),
            _ => $"{Shorten(parent, Math.Max(1, max - place.Length - 2))}, {place}"
        };
    }
}
