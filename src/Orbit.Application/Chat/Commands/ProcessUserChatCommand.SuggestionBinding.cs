using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static AiToolCall? ResolveSuggestedHabitCall(AiToolCall call, ToolExecutionAccumulator results) =>
        BindIssuedSuggestions(call, results) is { } bound ? ResolveHabitCall(bound, results) : null;

    private static AiToolCall? BindIssuedSuggestions(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (call.Name != "bulk_create_habits"
            || results.LastSuccessfulOperation("suggest_habits")?.Payload is not HabitSuggestionsCard card)
            return call;
        const string feedback = "The combined preview must contain each issued suggestion exactly once under its unchanged title. "
            + "Use the returned draft titles to identify suggestions; the server restores their issued fields. "
            + "Keep the person's explicit items alongside them. Later edits use revise_pending_changes.";
        AiToolCall? Invalid()
        {
            results.InvalidCalls[call.Id] = feedback;
            return null;
        }
        if (JsonNode.Parse(call.Args.GetRawText()) is not JsonObject arguments
            || arguments["habits"] is not JsonArray items || card.Drafts.Count == 0
            || card.Drafts.Select(draft => draft.Title).Distinct(StringComparer.Ordinal).Count() != card.Drafts.Count)
            return Invalid();
        var remaining = card.Drafts.ToDictionary(draft => draft.Title, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validator = new HabitSuggestionDraftValidator();
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject item || item["title"] is not JsonValue value
                || !value.TryGetValue<string>(out var title)) return Invalid();
            if (!remaining.TryGetValue(title, out var draft))
            {
                if (seen.Contains(title)) return Invalid();
                continue;
            }
            if (!validator.Validate(draft).IsValid) return Invalid();
            var target = draft.ExistingHabitId is { } id ? results.AllHabits.FirstOrDefault(habit => habit.Id == id) : null;
            if (draft.ExistingHabitId.HasValue && target is null) return Invalid();
            items[index] = JsonNode.Parse(PreviewHabitSuggestionCommandHandler.Arguments(draft, target).GetRawText());
            remaining.Remove(title);
            seen.Add(title);
        }
        return remaining.Count > 0 ? Invalid() : call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }
}
