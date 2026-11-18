using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class AccountChangeSet
{
    public const string OperationName = "apply_account_changes";
    public const string CapabilityId = "chat.changes";

    public sealed record Member(Guid PendingOperationId, string OperationId, JsonElement Arguments, string PreviewFingerprint, string? SourceFingerprint = null);
    public sealed record Reservation(Guid PendingOperationId, string Fingerprint);

    public static JsonElement BuildArguments(IEnumerable<PendingAgentOperationExecution> operations)
    {
        var members = operations.Select(operation => new Member(operation.PendingOperationId,
            operation.OperationId, operation.Arguments, operation.PreviewFingerprint!,
            AgentOperationFingerprint.Compute(operation.OperationId, operation.Arguments.GetRawText()))).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            operations = members,
            reservations = members.Select(member => new Reservation(member.PendingOperationId, member.SourceFingerprint!))
        });
    }

    public static IReadOnlyList<Reservation> Reservations(JsonElement arguments)
    {
        if (arguments.TryGetProperty("reservations", out var stored))
            return stored.Deserialize<List<Reservation>>() ?? [];
        return (Read(arguments) ?? []).Select(member => new Reservation(member.PendingOperationId,
            member.SourceFingerprint ?? AgentOperationFingerprint.Compute(member.OperationId, member.Arguments.GetRawText()))).ToArray();
    }

    public static IReadOnlyList<Member>? Read(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("operations", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < 1)
            return null;
        try
        {
            var members = values.Deserialize<List<Member>>();
            return members is not null && members.All(member => member.PendingOperationId != Guid.Empty
                && !string.IsNullOrWhiteSpace(member.OperationId) && member.OperationId != OperationName
                && member.Arguments.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(member.PreviewFingerprint))
                && members.DistinctBy(member => member.PendingOperationId).Count() == members.Count ? members : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
