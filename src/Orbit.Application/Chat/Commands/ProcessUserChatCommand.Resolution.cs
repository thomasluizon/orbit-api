using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool IsTagOnlyIntent(string message) =>
        Regex.IsMatch(message, @"\b(tags?|etiquetas?)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
        && !Regex.IsMatch(message, @"\b(crie|criar|create|add|adicione)\s+(?:(?:um|uma|new|a|one|novo|nova)\s+)?(?:h[aá]bitos?|habits?|rotina)\b",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private void SeedProposedHabitNames(Guid userId, ToolExecutionAccumulator results)
    {
        foreach (var name in execution.PendingOperationStore.GetPendingHabitCreations(userId))
            results.ProposedHabitNames.Add((name.ParentHabitId, HabitNameMatcher.Normalize(name.Title)));
    }

    private static ResponseCards ApplyResolutionToCards(ResponseCards cards, ToolExecutionAccumulator results, string? language)
    {
        if (results.MissingHabitNames.Count == 0)
            return cards;
        var message = AddResolutionQuestion(cards.AiMessage, results, language)!;
        return cards with
        {
            AiMessage = message,
            Parts = cards.Parts is null ? null : new ChatResponsePart[] { new ChatTextPart(message) }
                .Concat(cards.Parts.Where(part => part is not ChatTextPart)).ToArray()
        };
    }

    private static AiToolCall? ResolveHabitCall(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (JsonNode.Parse(call.Args.GetRawText()) is not JsonObject arguments)
            return call;

        if (!ResolveHabitIdentifiers(arguments, call.Name, results))
            return null;

        if (arguments["filter"] is JsonObject filter && filter["search"] is JsonValue searchValue
            && searchValue.TryGetValue<string>(out var search)
            && !results.Habits.Any(habit => HabitNameMatcher.Matches(habit.Title, search)))
        {
            results.MissingHabitNames.Add(search);
            return null;
        }

        if (call.Name is "create_habit" or "bulk_create_habits" or "create_sub_habit")
            return ResolveHabitCreation(call, arguments, results);

        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static bool ResolveHabitIdentifiers(JsonObject arguments, string operationId, ToolExecutionAccumulator results)
    {
        foreach (var key in new[] { "habit_id", "parent_habit_id" })
        {
            if (arguments[key] is not JsonValue value || !value.TryGetValue<string>(out var name)
                || Guid.TryParse(name, out _))
                continue;
            var habit = HabitNameMatcher.Resolve(results.Habits, name);
            if (habit is null)
            {
                results.MissingHabitNames.Add(name);
                return false;
            }
            arguments[key] = habit.Id.ToString();
        }

        if (arguments["habit_ids"] is JsonArray names)
        {
            var ids = new HashSet<Guid>();
            foreach (var value in names)
            {
                if (value is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
                    return true;
                if (Guid.TryParse(name, out var id))
                    ids.Add(id);
                else if (HabitNameMatcher.Resolve(results.Habits, name) is { } habit)
                    ids.Add(habit.Id);
                else
                    results.MissingHabitNames.Add(name);
            }
            if (ids.Count == 0)
                return false;
            if (operationId == "create_goal" && ids.Count != names.Count)
                return false;
            arguments["habit_ids"] = new JsonArray(ids.Order().Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray());
        }

        return true;
    }

    private static AiToolCall? ResolveHabitCreation(AiToolCall call, JsonObject arguments, ToolExecutionAccumulator results)
    {
        var items = call.Name == "bulk_create_habits" ? arguments["habits"] as JsonArray : new JsonArray(arguments.DeepClone());
        if (items is null)
            return call;
        if (items.Count == 1 && items[0] is JsonObject root
            && root["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var title)
            && root["sub_habits"] is JsonArray children
            && HabitNameMatcher.Resolve(results.Habits, title) is { } parent)
        {
            arguments = new JsonObject { ["parent_habit_id"] = parent.Id.ToString(), ["habits"] = children.DeepClone() };
            call = call with { Name = "bulk_create_habits" };
            items = arguments["habits"]!.AsArray();
        }
        else if (arguments["parent_habit_id"] is null && results.RequiresExistingParent)
        {
            results.MissingHabitNames.Add(items.FirstOrDefault()?["title"]?.ToString() ?? string.Empty);
            return null;
        }
        var proposed = NewHabitItems(items, arguments, results);
        if (proposed is null)
            return call;
        if (proposed.Count == 0)
            return null;
        if (call.Name == "bulk_create_habits")
            arguments["habits"] = proposed;
        else
            arguments = proposed[0]!.AsObject();
        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static JsonArray? NewHabitItems(JsonArray items, JsonObject arguments, ToolExecutionAccumulator results)
    {
        Guid? parentId = Guid.TryParse(arguments["parent_habit_id"]?.ToString(), out var parsed) ? parsed : null;
        var proposed = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["title"] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                return null;
            if (results.Habits.Any(habit => (parentId is null || habit.ParentHabitId == parentId)
                && HabitNameMatcher.Matches(habit.Title, name)))
                continue;
            if (results.IsTaggingIntent)
            {
                results.MissingHabitNames.Add(name);
                continue;
            }
            if (results.ProposedHabitNames.Any(previous => previous.ParentId == parentId
                && (HabitNameMatcher.Matches(previous.Name, name) || HabitNameMatcher.Matches(name, previous.Name))))
                continue;
            results.ProposedHabitNames.Add((parentId, HabitNameMatcher.Normalize(name)));
            proposed.Add(item.DeepClone());
        }
        return proposed;
    }

    private async Task CoalesceLogCallsAsync(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, Guid userId, CancellationToken ct)
    {
        var logs = calls.Select(call => resolvedCalls[call.Id]).OfType<AiToolCall>()
            .Where(call => call.Name == "bulk_log_habits").ToList();
        if (logs.Count < 2)
            return;
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var call in logs)
        {
            var expanded = await BulkHabitLogArguments.ExpandAsync(call.Args, data.HabitRepository,
                execution.UserDateService, userId, ct);
            if (expanded.IsFailure)
                return;
            foreach (var item in expanded.Value.GetProperty("revised_items").EnumerateArray())
                entries.TryAdd(Orbit.Domain.Common.AgentOperationFingerprint.Compute("log", item.GetRawText()), item.Clone());
        }
        resolvedCalls[logs[0].Id] = logs[0] with
        {
            Args = JsonSerializer.SerializeToElement(new { revised_items = entries.Values.ToArray() })
        };
        foreach (var call in logs.Skip(1))
            resolvedCalls[call.Id] = null;
    }

    private static void CoalesceTagCalls(Dictionary<string, AiToolCall?> calls)
    {
        var tags = calls.Values.OfType<AiToolCall>().Where(call => call.Name == "assign_tags")
            .GroupBy(call =>
            {
                var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
                arguments.Remove("habit_id");
                arguments.Remove("habit_ids");
                return Orbit.Domain.Common.AgentOperationFingerprint.Compute("tags", arguments.ToJsonString());
            });
        foreach (var group in tags.Select(group => group.ToList()).ToList())
        {
            if (group.Count < 2)
                continue;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in group)
            {
                if (call.Args.TryGetProperty("habit_id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
                if (call.Args.TryGetProperty("habit_ids", out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var value in values.EnumerateArray())
                        ids.Add(value.GetString()!);
            }
            var first = group[0];
            var arguments = JsonNode.Parse(first.Args.GetRawText())!.AsObject();
            arguments.Remove("habit_id");
            arguments["habit_ids"] = new JsonArray(ids.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            calls[first.Id] = first with { Args = JsonSerializer.SerializeToElement(arguments) };
            foreach (var call in group.Skip(1))
                calls[call.Id] = null;
        }
    }

    private static ToolCallOutcome ResolvedWithoutWrite(AiToolCall call, ToolExecutionAccumulator results) => new(
        new AiToolCallResult(call.Name, call.Id, true, null, null, null, new
        {
            status = "no_new_preview",
            existing_habits = results.Habits.Select(habit => new { habit.Id, habit.Title }).ToArray(),
            unresolved_names = results.MissingHabitNames.ToArray(),
            instruction = "Reuse existing habits. A repeated create or preview was refused. Ask about unresolved names without creating them."
        }), null, null, null, null);

    private static string? AddResolutionQuestion(string? message, ToolExecutionAccumulator results, string? language)
    {
        if (results.MissingHabitNames.Count == 0)
            return message;
        var names = string.Join(", ", results.MissingHabitNames);
        var question = Orbit.Application.Common.LocaleHelper.IsPortuguese(language)
            ? $"Não encontrei estes hábitos: {names}. Qual hábito você quer usar?"
            : $"I couldn't find these habits: {names}. Which habit would you like to use?";
        var previewMessage = Orbit.Application.Common.LocaleHelper.IsPortuguese(language)
            ? "Revise a prévia antes de aprovar." : "Review the preview before approving.";
        return results.PendingOperations.Count == 0 ? question : previewMessage + "\n\n" + question;
    }
}
