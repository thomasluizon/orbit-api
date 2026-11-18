using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    /// <summary>
    /// Previews each member of a change set through the plain path. The fingerprint of the set holds, for each member,
    /// its pending id, operation id, member preview fingerprint and reservation fingerprint, in place of the member
    /// arguments that the preview builds again: those differ by client context, and the stored set arguments pin what
    /// executes.
    /// </summary>
    private async Task<PendingOperationChangePreview?> PreviewChangeSetAsync(Guid userId, JsonElement arguments,
        AgentClientContext? clientContext, CancellationToken ct)
    {
        var members = AccountChangeSet.Read(arguments);
        if (members is null) return null;
        var reservations = AccountChangeSet.Reservations(arguments);
        var items = new List<PendingOperationItem>();
        var normalized = new List<AccountChangeSet.Member>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var result = await PreviewPlainAsync(userId, member.OperationId, member.Arguments, ct, clientContext);
            if (result.IsFailure || result.Value?.Items is not { Count: > 0 } children) return null;
            items.AddRange(children.Select(item => item with { ItemId = $"{index}/{item.ItemId}" }));
            normalized.Add(member with
            {
                Arguments = result.Value.ExecutionArguments ?? member.Arguments,
                PreviewFingerprint = result.Value.PreviewFingerprint!
            });
        }
        var view = normalized.Select(member => new
        {
            member.PendingOperationId,
            member.OperationId,
            member.PreviewFingerprint,
            Reservation = reservations.FirstOrDefault(reservation => reservation.PendingOperationId == member.PendingOperationId)?.Fingerprint
        });
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName,
            JsonSerializer.Serialize(view) + PreviewHashView.Serialize(items));
        return new PendingOperationChangePreview(items.SelectMany(item => item.Fields).ToArray(), items.Count, items, fingerprint)
        { ExecutionArguments = JsonSerializer.SerializeToElement(new { operations = normalized, reservations }) };
    }
}
