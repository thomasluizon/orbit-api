using static Orbit.Application.Chat.Tools.ToolArgumentNames;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool IsExplicitHabitCreate(string message) => Regex.IsMatch(message,
        @"\b(crie|cria|criar|create|add|adicione)\s+(?:(?:um|uma|o|a|os|as|new|one|novo|nova)\s+)?(?:h[aá]bitos?|habits?|rotina)\b",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static bool IsTagOnlyIntent(string message) =>
        Regex.IsMatch(message, @"\b(tags?|etiquetas?)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
        && !IsExplicitHabitCreate(message);

    private static bool RequestsExistingParent(string message, IReadOnlyList<Habit> habits)
    {
        var normalized = HabitNameMatcher.Normalize(message);
        if (Regex.IsMatch(normalized, @"\b(divida|dividir|split|break down)\b", RegexOptions.None, TimeSpan.FromSeconds(1)))
            return true;
        var asksForChildren = Regex.IsMatch(normalized, @"\b(sub habitos?|subhabitos?|children|child)\b",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        return habits.Any(habit => Regex.IsMatch(normalized,
            @"\b(under|dentro d[oa]|sob [oa]|n[oa] habito|ao habito)\s+" + Regex.Escape(HabitNameMatcher.Normalize(habit.Title)) + @"\b",
            RegexOptions.None, TimeSpan.FromSeconds(1))
            || asksForChildren && Regex.IsMatch(normalized,
                @"\b(no|na|ao|do|da|of|em)\s+(?:habito\s+)?" + Regex.Escape(HabitNameMatcher.Normalize(habit.Title)) + @"\b",
                RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    private void SeedProposedHabitNames(ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        if (request.History is not { Count: > 0 }) return;
        var proposals = execution.PendingOperationStore.GetPendingHabitCreations(request.UserId);
        var normalizedMessage = HabitNameMatcher.Normalize(request.Message);
        var asksForReplacement = Regex.IsMatch(normalizedMessage,
            @"\b(nova|new|outra|another)\s+(previa|preview)\b|\b(prepare|preparar|make)\s+(?:(?:um|uma|a)\s+)?(?:nova|new|outra|another)(?: one)?$|\b(again|de novo|reprepare)\b",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        foreach (var name in proposals)
        {
            var named = HabitNameMatcher.Matches(request.Message, name.Title);
            if (asksForReplacement && (named || proposals.Count == 1))
            {
                if (execution.PendingOperationStore.SupersedeHabitCreation(request.UserId, name.Title, name.ParentHabitId))
                    continue;
                results.BlockedReplacementNames.Add(name.Title);
                results.PendingDuplicateNames.Add(name.Title);
            }
            var key = (name.ParentHabitId, HabitNameMatcher.Normalize(name.Title));
            results.ProposedHabitNames.Add(key);
            results.PendingHabitNames[key] = name.Title;
        }
    }

    private static bool HasResolutionQuestion(ToolExecutionAccumulator results) =>
        results.MissingHabitNames.Count + results.ExistingDuplicateNames.Count
            + results.PendingDuplicateNames.Count + results.AmbiguousHabitNames.Count > 0;

    private static ResponseCards ApplyResolutionToCards(ResponseCards cards, ToolExecutionAccumulator results, string? language)
    {
        if (!HasResolutionQuestion(results))
            return cards;
        var message = AddResolutionQuestion(null, results, language)!;
        return cards with
        {
            AiMessage = message,
            ServerComposedText = true,
            Parts = cards.Parts is null ? null : new ChatResponsePart[] { new ChatTextPart(message) }
                .Concat(cards.Parts.Where(part => part is not ChatTextPart)).ToArray()
        };
    }

    private static AiToolCall? ResolveHabitCall(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (JsonNode.Parse(call.Args.GetRawText()) is not JsonObject arguments
            || new[] { HabitId, ParentHabitId }.Any(key => arguments[key] is not null
                && (arguments[key] is not JsonValue value || !value.TryGetValue<string>(out _)))
            || arguments["habit_ids"] is { } ids && (ids is not JsonArray array
                || array.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))))
        {
            results.InvalidCalls[call.Id] = "Tool arguments must be an object with string habit identifiers.";
            return null;
        }

        if (call.Name is "create_habit" or BulkCreateHabits or "create_sub_habit")
            return ResolveHabitCreation(call, arguments, results);

        if (!ResolveHabitIdentifiers(arguments, call.Name, results)
            || !ResolveSearchFilter(call.Name, arguments, results))
            return null;

        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static bool ResolveSearchFilter(string operationId, JsonObject arguments, ToolExecutionAccumulator results)
    {
        if (!BulkNoChangesOutcome.Supports(operationId) || arguments["revised_items"] is not null
            || arguments["filter"] is not JsonObject)
            return true;
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(JsonSerializer.SerializeToElement(arguments));
        if (error is not null || string.IsNullOrWhiteSpace(filter?.Search)
            || results.AllHabits.Any(habit => HabitNameMatcher.Matches(habit.Title, filter.Search)))
            return true;
        results.MissingHabitNames.Add(filter.Search);
        return false;
    }

    private static bool ResolveHabitIdentifiers(JsonObject arguments, string operationId, ToolExecutionAccumulator results)
    {
        foreach (var key in new[] { HabitId, ParentHabitId })
        {
            if (arguments[key] is not JsonValue value || !value.TryGetValue<string>(out var name)
                || Guid.TryParse(name, out _))
                continue;
            var habit = ResolveHabitName(results, name);
            if (habit is null)
                return false;
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
                else if (ResolveHabitName(results, name) is { } habit)
                    ids.Add(habit.Id);
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
        var items = call.Name == BulkCreateHabits ? arguments[ToolArgumentNames.Habits] as JsonArray : new JsonArray(arguments.DeepClone());
        if (items is null || items.Any(item => item is not JsonObject))
        {
            results.InvalidCalls[call.Id] = "Habits must be an array of objects.";
            return null;
        }
        items = HabitHeadingParents.UnwrapExistingParents(items, results.ParentHeadings);
        var allHaveHeadings = items.Count > 0 && items.OfType<JsonObject>()
            .All(item => HabitHeadingParents.ForItem(item, results.ParentHeadings) is not null);
        if (allHaveHeadings)
            arguments.Remove(ParentHabitId);
        else if (!ResolveHabitIdentifiers(arguments, call.Name, results))
            return null;
        if (!BindHeadingItems(call, items, results))
            return null;
        if (results.RequiresExistingParent && items.Count == 1 && items[0] is JsonObject root
            && root["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var title)
            && root[SubHabits] is JsonArray children
            && ResolveHabitName(results, title) is { } parent)
        {
            arguments = new JsonObject { [ParentHabitId] = parent.Id.ToString(), [ToolArgumentNames.Habits] = children.DeepClone() };
            call = call with { Name = BulkCreateHabits };
            items = arguments[ToolArgumentNames.Habits]!.AsArray();
        }
        else if (arguments[ParentHabitId] is null && results.RequiresExistingParent && !allHaveHeadings)
        {
            var name = (items.FirstOrDefault() as JsonObject)?["title"]?.ToString() ?? string.Empty;
            if (!results.AmbiguousHabitNames.ContainsKey(name))
                results.MissingHabitNames.Add(name);
            return null;
        }
        var proposed = NewHabitItems(items, arguments, results);
        if (proposed is null)
        {
            results.InvalidCalls[call.Id] = "Every habit must have a non-empty string title.";
            return null;
        }
        if (proposed.Count == 0)
            return null;
        return BindCreationOperation(call, arguments, proposed, allHaveHeadings);
    }

    private static bool BindHeadingItems(AiToolCall call, JsonArray items, ToolExecutionAccumulator results)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            if (HabitHeadingParents.ForItem(item, results.ParentHeadings) is { } heading)
                HabitHeadingParents.Bind(item, heading, removeInventedDescription: true);
            else if (item[ParentHabitId] is not null)
            {
                if (!results.RequiresExistingParent)
                    item[HabitHeadingParents.SuggestedParentField] = true;
                if (!ResolveHabitIdentifiers(item, call.Name, results)) return false;
            }
        }
        return true;
    }

    private static AiToolCall BindCreationOperation(AiToolCall call, JsonObject arguments, JsonArray proposed, bool hasHeadings)
    {
        if (call.Name == BulkCreateHabits)
            arguments[ToolArgumentNames.Habits] = proposed;
        else
        {
            arguments = proposed[0]!.AsObject();
            if (arguments[ParentHabitId] is not null)
            {
                if (Guid.TryParse(arguments[ParentHabitId]!.ToString(), out _))
                    call = call with { Name = "create_sub_habit" };
                else
                {
                    arguments = new JsonObject { [ToolArgumentNames.Habits] = proposed };
                    call = call with { Name = BulkCreateHabits };
                }
            }
            else if (hasHeadings)
            {
                arguments = new JsonObject { [ToolArgumentNames.Habits] = proposed };
                call = call with { Name = BulkCreateHabits };
            }
        }
        if (proposed.OfType<JsonObject>().Any(item => item[HabitId] is not null || item[HabitHeadingParents.SuggestedParentField] is not null))
        {
            call = call with { Name = BulkCreateHabits };
            arguments = new JsonObject { [ToolArgumentNames.Habits] = proposed.DeepClone() };
        }
        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static JsonArray? NewHabitItems(JsonArray items, JsonObject arguments, ToolExecutionAccumulator results)
    {
        if (items.Any(item => !HasHabitTitle(item))) return null;
        Guid? parentId = Guid.TryParse(arguments[ParentHabitId]?.ToString(), out var parsed) ? parsed : null;
        var proposed = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            if (results.IsTaggingIntent) continue;
            var name = item["title"]!.GetValue<string>();
            var itemParentId = Guid.TryParse(item[ParentHabitId]?.ToString(), out var itemParent) ? itemParent : parentId;
            BindExistingHabit(item, name, itemParentId, results);
            var key = (itemParentId, HabitNameMatcher.Normalize(name));
            if (!results.ProposedHabitNames.Add(key))
            {
                results.PendingDuplicateNames.Add(results.PendingHabitNames.GetValueOrDefault(key, name));
                continue;
            }
            proposed.Add(item.DeepClone());
        }
        return proposed;
    }

    private static bool HasHabitTitle(JsonNode? item) => item is JsonObject habit
        && habit["title"] is JsonValue value && value.TryGetValue<string>(out var title) && !string.IsNullOrWhiteSpace(title);

    private static void BindExistingHabit(JsonObject item, string name, Guid? parentId, ToolExecutionAccumulator results)
    {
        var matches = results.Habits.Where(habit => HabitNameMatcher.Normalize(habit.Title) == HabitNameMatcher.Normalize(name)
            && (parentId is null || habit.ParentHabitId == parentId)).ToArray();
        if (matches.Length > 1)
        {
            item[HabitId] = name;
            item[HabitHeadingParents.ExistingCandidatesField] = new JsonArray(matches.Select(habit => (JsonNode?)JsonValue.Create(habit.Id.ToString())).ToArray());
        }
        if (matches.Length != 1) return;
        var existing = matches[0];
        item[HabitId] = existing.Id.ToString();
        if (item[ParentHabitId] is null && existing.ParentHabitId is { } existingParent)
            item[ParentHabitId] = existingParent.ToString();
    }

    private async Task CoalesceLogCallsAsync(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, Guid userId, ToolExecutionAccumulator results, CancellationToken ct)
    {
        var logs = calls.Select(call => resolvedCalls[call.Id]).OfType<AiToolCall>()
            .Where(call => call.Name == "bulk_log_habits").ToList();
        if (logs.Count == 0)
            return;
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var matchedCalls = new List<AiToolCall>();
        foreach (var call in logs)
        {
            var expanded = await BulkHabitLogArguments.ExpandAsync(call.Args, data.HabitRepository,
                execution.UserDateService, userId, ct, results.AllHabits);
            if (expanded.IsFailure)
            {
                results.InvalidCalls[call.Id] = expanded.Error!;
                resolvedCalls[call.Id] = null;
                continue;
            }
            if (expanded.Value is not { } expandedArguments)
                continue;
            matchedCalls.Add(call);
            foreach (var item in expandedArguments.GetProperty("revised_items").EnumerateArray())
                entries.TryAdd(Orbit.Domain.Common.AgentOperationFingerprint.Compute("log", item.GetRawText()), item.Clone());
        }
        if (entries.Count == 0)
            return;
        var first = matchedCalls[0];
        resolvedCalls[first.Id] = first with
        {
            Args = JsonSerializer.SerializeToElement(new { revised_items = entries.Values.ToArray() })
        };
        foreach (var call in matchedCalls.Where(call => call.Id != first.Id))
        {
            if (resolvedCalls[call.Id] is not null)
                results.MergedCallIds.Add(call.Id);
            resolvedCalls[call.Id] = null;
        }
    }

    private static void CoalesceTagCalls(Dictionary<string, AiToolCall?> calls, ToolExecutionAccumulator results)
    {
        var tags = calls.Values.OfType<AiToolCall>().Where(call => call.Name == "assign_tags" && call.Args.ValueKind == JsonValueKind.Object)
            .GroupBy(call =>
            {
                var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
                arguments.Remove(HabitId);
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
                if (call.Args.TryGetProperty(HabitId, out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
                if (call.Args.TryGetProperty("habit_ids", out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var value in values.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String)
                            ids.Add(value.GetString()!);
            }
            var first = group[0];
            var arguments = JsonNode.Parse(first.Args.GetRawText())!.AsObject();
            arguments.Remove(HabitId);
            arguments["habit_ids"] = new JsonArray(ids.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            calls[first.Id] = first with { Args = JsonSerializer.SerializeToElement(arguments) };
            foreach (var call in group.Skip(1))
            {
                results.MergedCallIds.Add(call.Id);
                calls[call.Id] = null;
            }
        }
    }

    private static Habit? ResolveHabitName(ToolExecutionAccumulator results, string name)
    {
        var resolved = HabitNameMatcher.Resolve(results.Habits, name);
        if (resolved is not null)
            return resolved;
        var matches = results.Habits.Where(habit => HabitNameMatcher.Matches(habit.Title, name)).ToArray();
        if (matches.Length > 1)
            results.AmbiguousHabitNames[name] = matches.Select(habit => habit.Title).ToArray();
        else
            results.MissingHabitNames.Add(name);
        return null;
    }

    private static ToolCallOutcome ResolvedWithoutWrite(AiToolCall call, ToolExecutionAccumulator results, string? language)
    {
        var merged = results.MergedCallIds.Contains(call.Id);
        var error = results.InvalidCalls.GetValueOrDefault(call.Id);
        return new ToolCallOutcome(new AiToolCallResult(call.Name, call.Id, error is null, null, null,
            error, new
            {
                status = merged ? "merged_into_preview" : "no_new_preview",
                unresolved_names = results.MissingHabitNames.ToArray(),
                ambiguous_names = results.AmbiguousHabitNames,
                existing_duplicates = results.ExistingDuplicateNames.ToArray(),
                pending_duplicates = results.PendingDuplicateNames.ToArray(),
                instruction = merged
                    ? "This call was merged into the held preview for this intent. Do not repeat the call or prepare another preview. Nothing changes until approval."
                    : "No new preview was prepared. Ask about the returned missing, ambiguous or duplicate names. Ignore unsolicited creates for a tag-only request."
            }), error is null ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: ToolFailureMessage(language))
                .WithToolResultMessage(call, key: "toolFailure"), null, null, null);
    }

    private static string? AddResolutionQuestion(string? message, ToolExecutionAccumulator results, string? language)
    {
        if (!HasResolutionQuestion(results))
            return message;
        var portuguese = Orbit.Application.Common.LocaleHelper.IsPortuguese(language);
        var questions = new List<string>();
        if (results.MissingHabitNames.Count > 0)
        {
            var names = string.Join(", ", results.MissingHabitNames);
            questions.Add(results.MissingHabitNames.Count == 1
                ? portuguese ? $"Não encontrei este hábito: {names}. Qual hábito você quer usar?"
                    : $"I couldn't find this habit: {names}. Which habit would you like to use?"
                : portuguese ? $"Não encontrei estes hábitos: {names}. Quais hábitos você quer usar?"
                    : $"I couldn't find these habits: {names}. Which habits would you like to use?");
        }
        foreach (var (name, matches) in results.AmbiguousHabitNames)
            questions.Add(portuguese ? $"Para {name}, encontrei: {string.Join(", ", matches)}. Qual hábito você quer usar?"
                : $"For {name}, I found: {string.Join(", ", matches)}. Which habit would you like to use?");
        foreach (var name in results.ExistingDuplicateNames)
            questions.Add(portuguese ? $"O hábito {name} já existe. Você quer usar esse hábito ou criar outro com um nome diferente?"
                : $"The habit {name} already exists. Would you like to use it or create another with a different name?");
        foreach (var name in results.PendingDuplicateNames)
            questions.Add(results.BlockedReplacementNames.Contains(name)
                ? portuguese ? $"A prévia para criar {name} também inclui outros hábitos. Quais itens você quer manter na prévia?"
                    : $"The preview to create {name} also includes other habits. Which items would you like to keep in the preview?"
                : portuguese ? $"Você já tem uma prévia aguardando para criar {name}. Aprove a prévia ou me peça para preparar uma nova."
                    : $"You already have a preview waiting to create {name}. Approve it, or ask me to prepare a new one.");
        if (results.PendingOperations.Count > 0)
            questions.Insert(0, portuguese ? "Revise a prévia antes de aprovar." : "Review the preview before approving.");
        return string.Join("\n\n", questions);
    }
}
