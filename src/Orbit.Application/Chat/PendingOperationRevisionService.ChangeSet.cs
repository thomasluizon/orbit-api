using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationRevisionService
{
    private async Task<PendingOperationRevisionResult> ReviseChangeSetAsync(Guid userId,
        PendingAgentOperationExecution execution, RevisePendingOperationRequest request, CancellationToken ct)
    {
        var original = await previewer.PreviewAsync(userId, execution.OperationId, execution.Arguments, ct);
        if (original.IsFailure || original.Value?.Items is not { } items
            || original.Value.PreviewFingerprint != request.PreviewFingerprint)
            return Failure("stale_preview");
        var offered = items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        if (request.Items.Any(item => !offered.ContainsKey(item.ItemId))) return Failure("item_not_offered");
        if (EditError(request.Items, offered) is { } editError) return Failure(editError);
        var expected = AgentOperationFingerprint.Compute(execution.OperationId, execution.Arguments.GetRawText());
        if (request.Items.Count == 0)
            return store.Cancel(userId, execution.PendingOperationId, expected)
                ? new(true, null, null, null, Cancelled: true) : Failure("revision_conflict");
        var members = AccountChangeSet.Read(execution.Arguments);
        if (members is null) return Failure("invalid_change_set");
        var revisedMembers = await ReviseChangeSetMembersAsync(userId, members, request, items, ct);
        if (revisedMembers is null) return Failure("invalid_revision");
        var revised = JsonSerializer.SerializeToElement(new { operations = revisedMembers, reservations = AccountChangeSet.Reservations(execution.Arguments) });
        var preview = await previewer.PreviewAsync(userId, execution.OperationId, revised, ct);
        if (preview.IsFailure || preview.Value?.Items?.Count != request.Items.Count) return Failure("invalid_revision");
        var updated = store.Revise(userId, execution.PendingOperationId, expected, revised.GetRawText(),
            AgentOperationFingerprint.Compute(execution.OperationId, revised.GetRawText()), preview.Value.PreviewFingerprint!);
        return updated ? new(true, null, execution.PendingOperationId, preview.Value) : Failure("revision_conflict");
    }
    private async Task<List<AccountChangeSet.Member>?> ReviseChangeSetMembersAsync(Guid userId,
        IReadOnlyList<AccountChangeSet.Member> members, RevisePendingOperationRequest request,
        IReadOnlyList<PendingOperationItem> items, CancellationToken ct)
    {
        var revisedMembers = new List<AccountChangeSet.Member>();
        for (var index = 0; index < members.Count; index++)
        {
            var prefix = $"{index}/";
            var selected = request.Items.Where(item => item.ItemId.StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => item with { ItemId = item.ItemId[prefix.Length..] }).ToArray();
            if (selected.Length == 0) continue;
            var member = await ReviseChangeSetMemberAsync(userId, members[index], selected, items, prefix, ct);
            if (member is null) return null;
            revisedMembers.Add(member);
        }
        return revisedMembers;
    }

    private async Task<AccountChangeSet.Member?> ReviseChangeSetMemberAsync(Guid userId, AccountChangeSet.Member member,
        RevisedPendingOperationItem[] selected, IReadOnlyList<PendingOperationItem> items, string prefix, CancellationToken ct)
    {
        var childItems = items.Where(item => item.ItemId.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => item with { ItemId = item.ItemId[prefix.Length..] }).ToDictionary(item => item.ItemId);
        var arguments = BuildArguments(member.OperationId, member.Arguments, selected, childItems);
        if (arguments is null || !await AcceptsEditsAsync(userId, member.OperationId, arguments.Value, selected, ct))
            return null;
        var result = await previewer.PreviewAsync(userId, member.OperationId, arguments.Value, ct);
        if (result.IsFailure || result.Value?.Items?.Count != selected.Length
            || string.IsNullOrWhiteSpace(result.Value.PreviewFingerprint)) return null;
        return member with { Arguments = arguments.Value, PreviewFingerprint = result.Value.PreviewFingerprint };
    }

}
