using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools;

/// <summary>
/// Prepares intent and checks the original arguments without dispatching commands, reserving
/// approval, changing user or security state, or producing external effects. Returns null
/// when the tool cannot establish that preparation from its own execution semantics.
/// </summary>
public interface IPreparedIntentTool : IAiTool, IArgumentCheckTool
{
    Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct);
}

/// <summary>A tool-owned value produced by its ordinary command or domain preparation, before dispatch.</summary>
public sealed record PreparedToolIntent
{
    private readonly object _value;

    private PreparedToolIntent(object value, JsonElement executionArguments)
    {
        _value = value;
        ExecutionArguments = executionArguments.Clone();
    }

    [JsonIgnore]
    public JsonElement ExecutionArguments { get; }

    [JsonIgnore]
    public Result ArgumentValidation { get; init; } = Result.Success();

    public static PreparedToolIntent Create<T>(T value, JsonElement executionArguments) where T : notnull =>
        new(value, executionArguments);

    internal bool HasSameMeaning(PreparedToolIntent other) => _value.Equals(other._value);

    internal static IReadOnlyList<T> Values<T>(IEnumerable<T> values) => new IntentValues<T>(values);

    private sealed class IntentValues<T>(IEnumerable<T> values) : IReadOnlyList<T>
    {
        private readonly T[] _values = values.ToArray();
        public int Count => _values.Length;
        public T this[int index] => _values[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override bool Equals(object? obj) => obj is IntentValues<T> other && _values.SequenceEqual(other._values);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values)
                hash.Add(value);
            return hash.ToHashCode();
        }
    }
}

/// <summary>In-process evidence only. It is never accepted from tool arguments or serialized to a client.</summary>
public record PreparedToolOperationResponse : AgentExecuteOperationResponse
{
    public PreparedToolOperationResponse(AgentExecuteOperationResponse response,
        AgentExecuteOperationRequest request, PreparedToolIntent intent)
        : base(response.Operation, response.PendingOperation, response.PolicyDenial)
    {
        Request = request;
        Intent = intent;
    }

    [JsonIgnore]
    public AgentExecuteOperationRequest Request { get; }
    [JsonIgnore]
    public PreparedToolIntent Intent { get; }
}

/// <summary>Issued only by the gateway's argument check before policy, preview or tool execution.</summary>
public sealed record RejectedBeforeDispatchResponse : PreparedToolOperationResponse
{
    public RejectedBeforeDispatchResponse(AgentExecuteOperationResponse response,
        AgentExecuteOperationRequest request, PreparedToolIntent intent) : base(response, request, intent) { }
}
