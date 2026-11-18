using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.References;

/// <summary>A record that an <c>unknown_reference</c> error offers: its reference text and its title.</summary>
public sealed record ReferenceCandidate(string Value, string Title);

/// <summary>One argument value that names no record of the person, with the closest records of its type.</summary>
public sealed record ReferenceFailure(string Argument, ReferenceRecordType Type, string Value, IReadOnlyList<ReferenceCandidate> Closest);

/// <summary>
/// A tool call after the normalizer: every typed ID argument holds a full GUID, or <see cref="Failures"/> names each value
/// that resolves to no record of the person. A failed call keeps the arguments that the model sent.
/// </summary>
public sealed record NormalizedToolCall(AiToolCall Call, IReadOnlyList<ReferenceFailure> Failures)
{
    public const string ErrorCode = "unknown_reference";

    public bool IsResolved => Failures.Count == 0;

    /// <summary>The typed error that goes to the model as the tool result of the call. It names only the person's records.</summary>
    public string Error() => string.Join(" ", Failures.Select(failure => failure.Closest.Count > 0
            ? $"{ErrorCode}: {failure.Argument} \"{failure.Value}\" names no {Label(failure.Type)} of this person. Closest {Label(failure.Type)} references: {string.Join(", ", failure.Closest.Select(candidate => $"\"{candidate.Value}\" ({candidate.Title})"))}."
            : $"{ErrorCode}: {failure.Argument} \"{failure.Value}\" names no {Label(failure.Type)} of this person, and the person has none.")
        .Append("Send the call again with one of these references, or with a reference from the account index or a read result of this turn."));

    private static string Label(ReferenceRecordType type) => type switch
    {
        ReferenceRecordType.Habit => "habit",
        ReferenceRecordType.Goal => "goal",
        ReferenceRecordType.Tag => "tag",
        ReferenceRecordType.ChecklistTemplate => "checklist template",
        ReferenceRecordType.Notification => "notification",
        ReferenceRecordType.ApiKey => "API key",
        ReferenceRecordType.Skip => "skip",
        ReferenceRecordType.PendingOperation => "pending preview",
        _ => "preview item"
    };
}

/// <summary>
/// The one normalizer of the chat boundary. It reads each tool call of a model response before a guard or a tool runs
/// and resolves every typed ID argument, the JSON-string arguments (<c>edits_json</c> and the <c>arguments</c> of a
/// pickList item) and the GUID part of each preview item id.
/// </summary>
public sealed partial class ToolCallReferenceNormalizer(ReferenceBook book,
    Func<Guid, CancellationToken, Task<IReadOnlyList<PreviewItemReference>>> previewItems)
{
    public async Task<NormalizedToolCall> NormalizeAsync(AiToolCall call, CancellationToken ct)
    {
        if (call.Args.ValueKind != JsonValueKind.Object || JsonNode.Parse(call.Args.GetRawText()) is not JsonObject root)
            return new NormalizedToolCall(call, []);
        var failures = new List<ReferenceFailure>();
        var changed = await WalkAsync(root, call.Name == "ask", failures, ct);
        changed |= await ResolvePreviewItemsAsync(root, failures, ct);
        if (failures.Count > 0)
            return new NormalizedToolCall(call, failures);
        return new NormalizedToolCall(changed ? call with { Args = JsonSerializer.SerializeToElement(root) } : call, []);
    }

    /// <summary>
    /// Resolves each typed ID argument under the node. <paramref name="container"/> is the name of the argument that
    /// holds the node, so the option ids of an ask card resolve as habits and its question and item ids stay.
    /// </summary>
    private async Task<bool> WalkAsync(JsonNode? node, bool ask, List<ReferenceFailure> failures, CancellationToken ct,
        string? container = null)
    {
        var changed = false;
        if (node is JsonArray array)
        {
            foreach (var item in array.ToArray())
                changed |= await WalkAsync(item, ask, failures, ct, container);
            return changed;
        }
        if (node is not JsonObject fields)
            return false;
        foreach (var (name, value) in fields.ToArray())
        {
            if ((ask ? ReferenceArguments.AskRecordType(name, container) : null) is { } askType)
            {
                changed |= await ResolveValuesAsync(fields, name, askType, value, failures,
                    async text => ReferenceCodes.IsReference(text) ? OptionOutcome(text, await book.ResolveAsync(askType, text, ct)) : (text, []));
                continue;
            }
            if (ReferenceArguments.Unreferenced.Contains(name))
                continue;
            if (ReferenceArguments.Typed.TryGetValue(name, out var type))
            {
                if (type != ReferenceRecordType.PreviewItem)
                    changed |= await ResolveValuesAsync(fields, name, type, value, failures,
                        async text => ReferenceCodes.IsReference(text) ? Outcome(await book.ResolveAsync(type, text, ct)) : (text, []));
                continue;
            }
            if ((name == ReferenceArguments.EditsJson || ask && name == ReferenceArguments.PickListArguments)
                && value is JsonValue json && json.TryGetValue<string>(out var text))
            {
                changed |= await ResolveJsonStringAsync(fields, name, text, ask, failures, ct);
                continue;
            }
            changed |= await WalkAsync(value, ask, failures, ct, name);
        }
        return changed;
    }

    /// <summary>
    /// Resolves the JSON arguments of an operation that a string holds, with the same argument names as the operation
    /// schema. Text that is not a JSON object stays as it is, and the tool reports it.
    /// </summary>
    private async Task<bool> ResolveJsonStringAsync(JsonObject owner, string name, string text, bool ask,
        List<ReferenceFailure> failures, CancellationToken ct)
    {
        JsonNode? arguments;
        try { arguments = JsonNode.Parse(text); }
        catch (JsonException) { return false; }
        if (arguments is not JsonObject || !await WalkAsync(arguments, ask, failures, ct))
            return false;
        owner[name] = arguments.ToJsonString();
        return true;
    }

    private static (string? Value, IReadOnlyList<ReferenceCandidate> Closest) Outcome(ReferenceResolution resolution) =>
        resolution.Record is { } record
            ? (record.Id.ToString(), [])
            : (null, resolution.Closest.Select(candidate => new ReferenceCandidate(candidate.Id.ToString(), candidate.Title)).ToArray());

    /// <summary>
    /// The outcome of an option id of an ask card. A full GUID that names no habit of the person is turned down, because
    /// the ask guards read it as a record. Other text that names no habit, such as a <c>YYYY-MM-DD</c> date, is a free
    /// label and stays.
    /// </summary>
    private static (string? Value, IReadOnlyList<ReferenceCandidate> Closest) OptionOutcome(string text, ReferenceResolution resolution) =>
        resolution.IsResolved || Guid.TryParse(text.Trim(), out _) ? Outcome(resolution) : (text, []);

    /// <summary>Resolves one string or each string of an array. A null, an empty string or a value of another kind stays.</summary>
    private static async Task<bool> ResolveValuesAsync(JsonObject owner, string name, ReferenceRecordType type, JsonNode? value,
        List<ReferenceFailure> failures, Func<string, Task<(string? Value, IReadOnlyList<ReferenceCandidate> Closest)>> resolve)
    {
        if (value is JsonValue single && single.TryGetValue<string>(out var text))
        {
            if (await ResolveOneAsync(text) is not { } resolved || resolved == text)
                return false;
            owner[name] = resolved;
            return true;
        }
        if (value is not JsonArray array)
            return false;
        var changed = false;
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue item || !item.TryGetValue<string>(out var entry)
                || await ResolveOneAsync(entry) is not { } resolved || resolved == entry)
                continue;
            array[index] = resolved;
            changed = true;
        }
        return changed;

        async Task<string?> ResolveOneAsync(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
                return null;
            var (resolved, closest) = await resolve(entry);
            if (resolved is null)
                failures.Add(new ReferenceFailure(name, type, entry, closest));
            return resolved;
        }
    }

    /// <summary>
    /// Resolves each preview item id against the items of the preview that the call names. The member index of a change
    /// set and the date of a dated item stay, and only the GUID part resolves. An item id with no GUID part, such as the
    /// index of a new habit, reaches the tool unchanged.
    /// </summary>
    private async Task<bool> ResolvePreviewItemsAsync(JsonObject root, List<ReferenceFailure> failures, CancellationToken ct)
    {
        if (root["pending_operation_id"] is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !Guid.TryParse(text, out var pendingOperationId))
            return false;
        IReadOnlyList<PreviewItemReference>? items = null;
        return await WalkItemsAsync(root);

        async Task<bool> WalkItemsAsync(JsonNode? node)
        {
            var changed = false;
            if (node is JsonArray array)
            {
                foreach (var item in array.ToArray())
                    changed |= await WalkItemsAsync(item);
                return changed;
            }
            if (node is not JsonObject fields)
                return false;
            foreach (var (name, value) in fields.ToArray())
            {
                if (ReferenceArguments.Typed.GetValueOrDefault(name, ReferenceRecordType.Habit) == ReferenceRecordType.PreviewItem)
                {
                    items ??= await previewItems(pendingOperationId, ct);
                    if (items.Count > 0)
                        changed |= await ResolveValuesAsync(fields, name, ReferenceRecordType.PreviewItem, value, failures,
                            text => Task.FromResult(ResolveItem(text, items)));
                }
                else if (name != ReferenceArguments.EditsJson)
                    changed |= await WalkItemsAsync(value);
            }
            return changed;
        }
    }

    private static (string? Value, IReadOnlyList<ReferenceCandidate> Closest) ResolveItem(string text, IReadOnlyList<PreviewItemReference> items)
    {
        var parts = Split(text);
        if (items.Any(item => item.ItemId == text) || !ReferenceCodes.IsReference(parts.Core))
            return (text, []);
        var shaped = items.Select(item => (Item: item, Parts: Split(item.ItemId)))
            .Where(pair => pair.Parts.Prefix == parts.Prefix && pair.Parts.Suffix == parts.Suffix && Guid.TryParse(pair.Parts.Core, out _))
            .ToArray();
        var resolution = ReferenceCodes.Resolve(
            shaped.Select(pair => new ReferenceRecord(Guid.Parse(pair.Parts.Core), pair.Item.Title)).DistinctBy(record => record.Id).ToArray(),
            parts.Core);
        if (resolution.Record is { } record)
            return (shaped.First(pair => Guid.Parse(pair.Parts.Core) == record.Id).Item.ItemId, []);
        var closest = resolution.Closest.Select(candidate => shaped.First(pair => Guid.Parse(pair.Parts.Core) == candidate.Id).Item)
            .Concat(items).DistinctBy(item => item.ItemId).Take(3);
        return (null, closest.Select(item => new ReferenceCandidate(item.ItemId, item.Title)).ToArray());
    }

    private static (string Prefix, string Core, string Suffix) Split(string itemId)
    {
        var match = ItemIdShape().Match(itemId);
        return (match.Groups["prefix"].Value, match.Groups["core"].Value, match.Groups["suffix"].Value);
    }

    [GeneratedRegex(@"^(?<prefix>\d+/)?(?<core>.*?)(?<suffix>:\d{4}-\d{2}-\d{2})?$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ItemIdShape();
}
