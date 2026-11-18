using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>One change of the typed change list: its id, its kind and the arguments of that kind.</summary>
public sealed record ProposedChange(string Id, string Kind, JsonElement Arguments);

/// <summary>
/// One typed error of a change list. <see cref="Change"/> is the id of the change, <see cref="Path"/> the argument that
/// the error names, or an empty text when the error names the whole change.
/// </summary>
public sealed record ChangeError(
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("allowed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Allowed = null,
    [property: JsonPropertyName("candidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Candidates = null);

/// <summary>
/// The result of one change after its write tool ran: the preview that it prepared, or its error. A change that the turn
/// merged into another change of the same response prepares no preview of its own and has no error.
/// </summary>
public sealed record ChangeResult(string ChangeId, PendingAgentOperation? Preview, ChangeError? Error);

/// <summary>
/// The changes of a list that can run, the changes that wait for the answer of an <c>ask</c> in the same response, and
/// the errors that the list holds before any write runs.
/// </summary>
public sealed record ChangeListCheck(IReadOnlyList<AiToolCall> Runnable, IReadOnlyList<ChangeError> Waiting,
    IReadOnlyList<ChangeError> Errors);

/// <summary>The previews of a compiled change list, or its errors. A list with an error holds no preview.</summary>
public sealed record ChangeListOutcome(IReadOnlyList<PendingAgentOperation> Previews, IReadOnlyList<ChangeError> Errors,
    IReadOnlyList<ChangeError> Waiting)
{
    public bool IsSuccess => Errors.Count == 0;
}

/// <summary>
/// Compiles the typed change list of one model response as one unit, through the write tools and the executor that exist.
/// All atomic changes form one change-set preview, each other change gets its own preview, and one response holds
/// <see cref="MaxPreviews"/> previews at most. If one change fails, the list holds nothing and returns every error in
/// one typed list.
/// </summary>
public sealed class ChangeListCompiler(AiToolRegistry registry)
{
    public const int MaxPreviews = 3;
    public const string NotPrepared = "changes_not_prepared";
    public const string UnknownKind = "unknown_kind";
    public const string DuplicateChange = "duplicate_change";
    public const string WaitsForAnswer = "waits_for_answer";
    public const string TooManyPreviews = "too_many_previews";
    public const string MissingField = "missing_field";
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>
    /// Checks the list before any write runs. A change of an unknown kind and a change id that repeats are errors. A
    /// change whose arguments name a record of <paramref name="askRecords"/> waits for the answer: it does not run, and it
    /// is not an error of the list.
    /// </summary>
    public ChangeListCheck Check(IReadOnlyList<ProposedChange> changes, IReadOnlySet<Guid> askRecords)
    {
        var kinds = ChangeKinds.All(registry).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var runnable = new List<AiToolCall>();
        var waiting = new List<ChangeError>();
        var errors = new List<ChangeError>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Id) || !ids.Add(change.Id))
                errors.Add(new(change.Id, "id", DuplicateChange, "Each change needs its own id."));
            else if (!kinds.Contains(change.Kind))
                errors.Add(new(change.Id, "kind", UnknownKind, $"{change.Kind} is not a change kind.", [.. kinds.Order(StringComparer.Ordinal)]));
            else if (change.Arguments.ValueKind != JsonValueKind.Object)
                errors.Add(new(change.Id, "arguments", InvalidArguments, "The arguments of a change are an object."));
            else if (RecordIds(change.Arguments).Where(askRecords.Contains).ToArray() is { Length: > 0 } held)
                waiting.Add(new(change.Id, "arguments", WaitsForAnswer, "This change names a record of the question in the same response.",
                    Candidates: [.. held.Select(id => id.ToString())]));
            else
                runnable.Add(new AiToolCall(change.Kind, change.Id, change.Arguments));
        }
        return new(runnable, waiting, errors);
    }

    /// <summary>
    /// Decides the list after its writes ran: the error of each change that failed, and <c>too_many_previews</c> when the
    /// previews of the list need more than <see cref="MaxPreviews"/> cards. Older previews do not count.
    /// </summary>
    public async Task<IReadOnlyList<ChangeError>> SettleAsync(Guid userId, IReadOnlyList<ChangeResult> results,
        IPendingAgentOperationStore store, CancellationToken ct)
    {
        var errors = results.Where(result => result.Error is not null).Select(result => result.Error!).ToList();
        if (errors.Count > 0) return errors;
        var atomic = new List<ChangeResult>();
        var separate = new List<ChangeResult>();
        foreach (var result in results.Where(result => result.Preview is not null))
        {
            var stored = store.GetExecution(userId, result.Preview!.Id);
            if (stored is not null && await AiToolAtomicity.SupportsAsync(registry.GetTool(stored.OperationId), userId, stored.Arguments, ct))
                atomic.Add(result);
            else
                separate.Add(result);
        }
        var cards = (atomic.Count >= 2 ? 1 : atomic.Count) + separate.Count;
        if (cards <= MaxPreviews) return [];
        var beyond = separate.Skip(MaxPreviews - (atomic.Count >= 2 ? 1 : atomic.Count)).First();
        return [new(beyond.ChangeId, string.Empty, TooManyPreviews,
            $"One response holds at most {MaxPreviews} previews; this list needs {cards}.")];
    }

    /// <summary>
    /// Compiles a change list through <paramref name="dispatch"/>, which runs one write tool call and returns its result.
    /// A list with an error withdraws each preview that it prepared, so it holds nothing.
    /// </summary>
    public async Task<ChangeListOutcome> CompileAsync(Guid userId, IReadOnlyList<ProposedChange> changes, IReadOnlySet<Guid> askRecords,
        Func<AiToolCall, CancellationToken, Task<ChangeResult>> dispatch, IPendingAgentOperationStore store, CancellationToken ct)
    {
        var check = Check(changes, askRecords);
        var results = new List<ChangeResult>();
        foreach (var call in check.Runnable)
            results.Add(await dispatch(call, ct));
        var errors = check.Errors.Concat(await SettleAsync(userId, results, store, ct)).ToArray();
        var previews = results.Where(result => result.Preview is not null).Select(result => result.Preview!).ToArray();
        if (errors.Length == 0)
            return new(previews, [], check.Waiting);
        foreach (var preview in previews)
            Withdraw(userId, preview.Id, store);
        return new([], errors, check.Waiting);
    }

    /// <summary>
    /// The typed error of a change call that holds no readable change. <paramref name="callId"/> is the id of the call,
    /// because the list has no change to name.
    /// </summary>
    public static ChangeError EmptyList(string callId) => new(callId, ChangeDeclarations.ChangesArgument, InvalidArguments,
        $"The change list holds no change. Send one item in {ChangeDeclarations.ChangesArgument} for each change.");

    /// <summary>Withdraws a preview that a failed change list prepared.</summary>
    public static void Withdraw(Guid userId, Guid pendingOperationId, IPendingAgentOperationStore store)
    {
        if (store.GetExecution(userId, pendingOperationId) is { } stored)
            store.Withdraw(userId, pendingOperationId,
                AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()));
    }

    /// <summary>
    /// The typed error of a change whose write tool did not prepare a preview, from the result that the executor gave.
    /// </summary>
    public static ChangeError ErrorOf(string changeId, AgentOperationResult? operation, AiToolCallResult toolResult, string? failureKey)
    {
        var message = toolResult.Error ?? operation?.PolicyReason ?? "The change could not be prepared.";
        var code = operation switch
        {
            { ErrorCode: { Length: > 0 } errorCode } => errorCode,
            { Status: AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy, PolicyReason: { Length: > 0 } reason } => reason,
            _ => failureKey ?? InvalidArguments
        };
        return new(changeId, string.Empty, code, message);
    }

    /// <summary>The record ids that the arguments of a change or of an <c>ask</c> name, also inside JSON-text values.</summary>
    public static IEnumerable<Guid> RecordIds(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => RecordIds(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(RecordIds),
        JsonValueKind.String when value.TryGetGuid(out var id) => [id],
        JsonValueKind.String when value.GetString() is { Length: > 1 } text && text[0] is '{' or '[' => ParsedRecordIds(text),
        _ => []
    };

    private static IEnumerable<Guid> ParsedRecordIds(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return RecordIds(document.RootElement).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
