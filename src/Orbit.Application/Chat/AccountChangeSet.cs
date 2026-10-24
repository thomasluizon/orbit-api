using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class AccountChangeSet
{
    public const string OperationId = "apply_account_changes";
    public const string CapabilityId = "chat.changes";

    public sealed record Member(Guid PendingOperationId, string OperationId, JsonElement Arguments, string PreviewFingerprint, string? SourceFingerprint = null);

    public static JsonElement Arguments(IEnumerable<PendingAgentOperationExecution> operations) =>
        JsonSerializer.SerializeToElement(new
        {
            operations = operations.Select(operation => new Member(operation.PendingOperationId,
                operation.OperationId, operation.Arguments, operation.PreviewFingerprint!,
                AgentOperationFingerprint.Compute(operation.OperationId, operation.Arguments.GetRawText())))
        });

    public static IReadOnlyList<Member>? Read(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("operations", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < 1)
            return null;
        try
        {
            var members = values.Deserialize<List<Member>>();
            return members is not null && members.All(member => member.PendingOperationId != Guid.Empty
                && !string.IsNullOrWhiteSpace(member.OperationId) && member.OperationId != OperationId
                && member.Arguments.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(member.PreviewFingerprint))
                && members.DistinctBy(member => member.PendingOperationId).Count() == members.Count ? members : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<PendingOperationChangePreview?> PreviewAsync(Guid userId, JsonElement arguments,
        IPendingOperationChangePreviewer previewer, CancellationToken ct)
    {
        var members = Read(arguments);
        if (members is null) return null;
        var items = new List<PendingOperationItem>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var result = await previewer.PreviewAsync(userId, member.OperationId, member.Arguments, ct);
            if (result.IsFailure || result.Value?.Items is not { Count: > 0 } children) return null;
            items.AddRange(children.Select(item => item with { ItemId = $"{index}/{item.ItemId}" }));
        }
        var fingerprint = AgentOperationFingerprint.Compute(OperationId,
            arguments.GetRawText() + JsonSerializer.Serialize(items));
        return new PendingOperationChangePreview(items.SelectMany(item => item.Fields).ToArray(), items.Count, items, fingerprint);
    }
}
