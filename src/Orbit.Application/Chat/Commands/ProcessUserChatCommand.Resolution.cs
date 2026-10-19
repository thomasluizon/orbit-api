using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private const string PortuguesePendingQuestion = "Você já tem uma prévia para criar";
    private const string EnglishPendingQuestion = "You already have a preview to create";

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
        var previousReply = request.History?.LastOrDefault(item =>
            ChatHistoryMessage.NormalizeRole(item.Role) == ChatHistoryMessage.AssistantRole)?.Content;
        var proposals = execution.PendingOperationStore.GetPendingHabitCreations(request.UserId);
        foreach (var name in proposals)
        {
            var discussed = previousReply is not null
                && (previousReply.Contains(PortuguesePendingQuestion, StringComparison.Ordinal)
                    || previousReply.Contains(EnglishPendingQuestion, StringComparison.Ordinal))
                && HabitNameMatcher.Matches(previousReply, name.Title);
            var named = HabitNameMatcher.Matches(request.Message, name.Title);
            var asksForReplacement = Regex.IsMatch(HabitNameMatcher.Normalize(request.Message),
                @"\b(nova|new|outra|another)\s+(previa|preview)\b|\b(prepare|preparar|make)\s+(?:(?:um|uma|a)\s+)?(?:nova|new|outra|another)(?: one)?$",
                RegexOptions.None, TimeSpan.FromSeconds(1));
            var explicitlyReplaces = asksForReplacement && (named || proposals.Count == 1)
                || discussed && named && Regex.IsMatch(request.Message,
                    @"\b(crie|cria|criar|create|prepare|preparar|nova|new|again|de novo)\b",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (explicitlyReplaces)
            {
                execution.PendingOperationStore.SupersedeHabitCreation(request.UserId, name.Title, name.ParentHabitId);
                continue;
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
        if (JsonNode.Parse(call.Args.GetRawText()) is not JsonObject arguments
            || new[] { "habit_id", "parent_habit_id" }.Any(key => arguments[key] is not null
                && (arguments[key] is not JsonValue value || !value.TryGetValue<string>(out _)))
            || arguments["habit_ids"] is { } ids && (ids is not JsonArray array
                || array.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))))
        {
            results.InvalidCalls[call.Id] = "Tool arguments must be an object with string habit identifiers.";
            return null;
        }

        if (arguments["parent_habit_id"] is not null && !results.RequiresExistingParent
            && call.Name is "create_habit" or "bulk_create_habits" or "create_sub_habit")
        {
            results.InvalidCalls[call.Id] = "Only assign an existing parent when the person asks for children under it.";
            return null;
        }
        if (!ResolveHabitIdentifiers(arguments, call.Name, results)
            || !ResolveSearchFilter(call.Name, arguments, results))
            return null;

        if (call.Name is "create_habit" or "bulk_create_habits" or "create_sub_habit")
            return ResolveHabitCreation(call, arguments, results);

        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static bool ResolveSearchFilter(string operationId, JsonObject arguments, ToolExecutionAccumulator results)
    {
        if (!operationId.StartsWith("bulk_", StringComparison.Ordinal) || operationId == "bulk_create_habits"
            || arguments["filter"] is not JsonObject filter || filter["search"] is not JsonValue searchValue
            || !searchValue.TryGetValue<string>(out var search) || string.IsNullOrWhiteSpace(search))
            return true;
        var parsed = BulkHabitToolArguments.ParseRequiredFilter(JsonSerializer.SerializeToElement(arguments));
        if (parsed.Error is not null || parsed.Filter is null || BulkHabitSelection.Select(results.AllHabits, parsed.Filter).Count > 0)
            return true;
        results.MissingHabitNames.Add(search);
        return false;
    }

    private static bool ResolveHabitIdentifiers(JsonObject arguments, string operationId, ToolExecutionAccumulator results)
    {
        foreach (var key in new[] { "habit_id", "parent_habit_id" })
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
        var items = call.Name == "bulk_create_habits" ? arguments["habits"] as JsonArray : new JsonArray(arguments.DeepClone());
        if (items is null || items.Any(item => item is not JsonObject))
        {
            results.InvalidCalls[call.Id] = "Habits must be an array of objects.";
            return null;
        }
        if (results.RequiresExistingParent && items.Count == 1 && items[0] is JsonObject root
            && root["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var title)
            && root["sub_habits"] is JsonArray children
            && ResolveHabitName(results, title) is { } parent)
        {
            arguments = new JsonObject { ["parent_habit_id"] = parent.Id.ToString(), ["habits"] = children.DeepClone() };
            call = call with { Name = "bulk_create_habits" };
            items = arguments["habits"]!.AsArray();
        }
        else if (arguments["parent_habit_id"] is null && results.RequiresExistingParent)
        {
            var name = (items.FirstOrDefault() as JsonObject)?["title"]?.ToString() ?? string.Empty;
            if (!results.AmbiguousHabitNames.ContainsKey(name))
                results.MissingHabitNames.Add(name);
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
            if (results.IsTaggingIntent)
                continue;
            var existing = results.Habits.FirstOrDefault(habit => habit.ParentHabitId == parentId
                && HabitNameMatcher.Normalize(habit.Title) == HabitNameMatcher.Normalize(name));
            if (existing is not null)
            {
                results.ExistingDuplicateNames.Add(existing.Title);
                continue;
            }
            if (results.ProposedHabitNames.Contains((parentId, HabitNameMatcher.Normalize(name))))
            {
                results.PendingDuplicateNames.Add(results.PendingHabitNames.GetValueOrDefault(
                    (parentId, HabitNameMatcher.Normalize(name)), name));
                continue;
            }
            results.ProposedHabitNames.Add((parentId, HabitNameMatcher.Normalize(name)));
            proposed.Add(item.DeepClone());
        }
        return proposed;
    }

    private async Task CoalesceLogCallsAsync(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, Guid userId, ToolExecutionAccumulator results, CancellationToken ct)
    {
        var logs = calls.Select(call => resolvedCalls[call.Id]).OfType<AiToolCall>()
            .Where(call => call.Name == "bulk_log_habits").ToList();
        if (logs.Count == 0)
            return;
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
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
            foreach (var item in expanded.Value.GetProperty("revised_items").EnumerateArray())
                entries.TryAdd(Orbit.Domain.Common.AgentOperationFingerprint.Compute("log", item.GetRawText()), item.Clone());
        }
        if (entries.Count == 0)
            return;
        var first = logs.First(call => resolvedCalls[call.Id] is not null);
        resolvedCalls[first.Id] = first with
        {
            Args = JsonSerializer.SerializeToElement(new { revised_items = entries.Values.ToArray() })
        };
        foreach (var call in logs.Where(call => call.Id != first.Id))
            resolvedCalls[call.Id] = null;
    }

    private static void CoalesceTagCalls(Dictionary<string, AiToolCall?> calls)
    {
        var tags = calls.Values.OfType<AiToolCall>().Where(call => call.Name == "assign_tags" && call.Args.ValueKind == JsonValueKind.Object)
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
                        if (value.ValueKind == JsonValueKind.String)
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

    private static ToolCallOutcome ResolvedWithoutWrite(AiToolCall call, ToolExecutionAccumulator results) => new(
        new AiToolCallResult(call.Name, call.Id, !results.InvalidCalls.ContainsKey(call.Id), null, null,
            results.InvalidCalls.GetValueOrDefault(call.Id), new
            {
                status = "no_new_preview",
                unresolved_names = results.MissingHabitNames.ToArray(),
                ambiguous_names = results.AmbiguousHabitNames,
                existing_duplicates = results.ExistingDuplicateNames.ToArray(),
                pending_duplicates = results.PendingDuplicateNames.ToArray(),
                instruction = "No new preview was prepared. Ask about the returned missing, ambiguous or duplicate names. Ignore unsolicited creates for a tag-only request."
            }), null, null, null, null);

    private static string? AddResolutionQuestion(string? message, ToolExecutionAccumulator results, string? language)
    {
        if (!HasResolutionQuestion(results))
            return message;
        var portuguese = Orbit.Application.Common.LocaleHelper.IsPortuguese(language);
        var questions = new List<string>();
        if (results.MissingHabitNames.Count > 0)
        {
            var names = string.Join(", ", results.MissingHabitNames);
            questions.Add(portuguese ? $"Não encontrei estes hábitos: {names}. Qual hábito você quer usar?"
                : $"I couldn't find these habits: {names}. Which habit would you like to use?");
        }
        foreach (var (name, matches) in results.AmbiguousHabitNames)
            questions.Add(portuguese ? $"Para {name}, encontrei: {string.Join(", ", matches)}. Qual hábito você quer usar?"
                : $"For {name}, I found: {string.Join(", ", matches)}. Which habit would you like to use?");
        foreach (var name in results.ExistingDuplicateNames)
            questions.Add(portuguese ? $"O hábito {name} já existe. Você quer usar esse hábito ou criar outro com um nome diferente?"
                : $"The habit {name} already exists. Would you like to use it or create another with a different name?");
        foreach (var name in results.PendingDuplicateNames)
            questions.Add(portuguese ? $"{PortuguesePendingQuestion} {name} aguardando. Aprove a prévia ou me diga para preparar uma nova."
                : $"{EnglishPendingQuestion} {name} waiting. Approve it, or tell me to prepare a new one.");
        if (results.PendingOperations.Count > 0)
            questions.Insert(0, portuguese ? "Revise a prévia antes de aprovar." : "Review the preview before approving.");
        return string.Join("\n\n", questions);
    }
}
