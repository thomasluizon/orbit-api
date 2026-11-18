using Orbit.Application.Chat.References;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private ShortReferenceTurn? _referenceTurn;

    /// <summary>
    /// The model of the turn. Each call goes through the chat boundary of short references: model text holds references,
    /// and each tool call reaches a guard or a tool with full GUIDs.
    /// </summary>
    private IAiIntentService Model => _referenceTurn?.Model ?? ai.IntentService;

    private ShortReferenceTurn BeginReferenceTurn(ProcessUserChatCommand request)
    {
        var book = new ReferenceBook((type, ct) => LoadReferenceRecordsAsync(request.UserId, type, ct));
        var normalizer = new ToolCallReferenceNormalizer(book, (id, ct) => LoadPreviewItemsAsync(request, id, ct));
        return _referenceTurn = new ShortReferenceTurn(book, normalizer, references?.ShortReferences == true, ai.IntentService);
    }

    /// <summary>
    /// Gives the book of the turn the records that the turn context already read: every habit and tag, and the active
    /// goals. A goal reference that misses, or a goal GUID in model text, reads every goal of the person once.
    /// </summary>
    private void SeedReferences(ChatContext context)
    {
        if (_referenceTurn is not { } turn)
            return;
        turn.Book.Seed(ReferenceRecordType.Habit, context.UserHabits.Select(habit => new ReferenceRecord(habit.Id, habit.Title)));
        turn.Book.Seed(ReferenceRecordType.Tag, context.UserTags.Select(tag => new ReferenceRecord(tag.Id, tag.Name)));
        turn.Book.Seed(ReferenceRecordType.Goal, context.ActiveGoals.Select(goal => new ReferenceRecord(goal.Id, goal.Title)),
            complete: false);
    }

    /// <summary>Gives the book of the turn the previews that await approval, which the prompt already read.</summary>
    private void SeedPendingReferences(IEnumerable<PendingAgentOperationExecution> awaiting) =>
        _referenceTurn?.Book.Seed(ReferenceRecordType.PendingOperation,
            awaiting.Select(pending => new ReferenceRecord(pending.PendingOperationId, pending.OperationId)));

    /// <summary>
    /// Tells the book of the turn that a tool round wrote a record: a write that ran, or a write that a preview holds. A
    /// later miss then reads the types that the book read before the write.
    /// </summary>
    private void NoteReferenceWrites(IEnumerable<ToolCallOutcome> outcomes)
    {
        if (_referenceTurn is { } turn && outcomes.Any(outcome => outcome.PendingOperation is not null
            || outcome.OperationResult is { Status: AgentOperationStatus.Succeeded } operation
            && ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }))
            turn.Book.NoteWrite();
    }

    private async Task<IReadOnlyList<ReferenceRecord>> LoadReferenceRecordsAsync(Guid userId, ReferenceRecordType type, CancellationToken ct)
    {
        switch (type)
        {
            case ReferenceRecordType.Habit:
                return (await data.HabitRepository.FindAsync(habit => habit.UserId == userId, null, ct))
                    .Select(habit => new ReferenceRecord(habit.Id, habit.Title)).ToArray();
            case ReferenceRecordType.Goal:
                return (await data.GoalRepository.FindAsync(goal => goal.UserId == userId, null, ct))
                    .Select(goal => new ReferenceRecord(goal.Id, goal.Title)).ToArray();
            case ReferenceRecordType.Tag:
                return (await data.TagRepository.FindAsync(tag => tag.UserId == userId, ct))
                    .Select(tag => new ReferenceRecord(tag.Id, tag.Name)).ToArray();
            case ReferenceRecordType.ChecklistTemplate:
                return (await data.ChecklistTemplateRepository.FindAsync(template => template.UserId == userId, ct))
                    .Select(template => new ReferenceRecord(template.Id, template.Name)).ToArray();
            case ReferenceRecordType.Notification when references is not null:
                return await references.Notifications.ProjectAsync(notification => notification.UserId == userId,
                    query => query.Select(notification => new ReferenceRecord(notification.Id, notification.Title)), ct);
            case ReferenceRecordType.ApiKey when references is not null:
                return await references.ApiKeys.ProjectAsync(key => key.UserId == userId,
                    query => query.Select(key => new ReferenceRecord(key.Id, key.Name)), ct);
            case ReferenceRecordType.Skip when references is not null:
                return await references.Skips.ProjectAsync(skip => skip.UserId == userId && !skip.IsUndone,
                    query => query.Select(skip => new ReferenceRecord(skip.Id, "skip")), ct);
            case ReferenceRecordType.PendingOperation:
                return (await execution.PendingOperationStore.GetAwaitingAsync(userId, ct))
                    .Select(pending => new ReferenceRecord(pending.PendingOperationId, pending.OperationId)).ToArray();
            default:
                return [];
        }
    }

    /// <summary>The items of a stored preview, which a preview item id of a revision names.</summary>
    private async Task<IReadOnlyList<PreviewItemReference>> LoadPreviewItemsAsync(ProcessUserChatCommand request, Guid pendingOperationId,
        CancellationToken ct)
    {
        if (execution.PendingOperationStore.GetExecution(request.UserId, pendingOperationId) is not { } stored)
            return [];
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var preview = await previewer.PreviewAsync(request.UserId, stored.OperationId, stored.Arguments, ct, request.ClientContext);
        return preview.IsSuccess && preview.Value?.Items is { } items
            ? items.Select(item => new PreviewItemReference(item.ItemId, item.EntityName)).ToArray()
            : [];
    }

    /// <summary>
    /// The outcome of a call whose arguments name a record that the person does not have, before a guard or a tool runs.
    /// The turn treats <c>unknown_reference</c> as an ownership denial: one correction beside a change of the turn, or no
    /// preview. The client action reads as that denial; only the model gets the closest references. An <c>ask</c> call
    /// fails in its own path.
    /// </summary>
    private ToolCallOutcome? ReferenceFailureOutcome(AiToolCall call)
    {
        if (call.Name == "ask" || _referenceTurn?.FailureOf(call.Id) is not { } failure
            || ai.ToolRegistry.GetTool(call.Name) is not { } tool)
            return null;
        var error = failure.Error();
        var reason = $"{NormalizedToolCall.ErrorCode}:{call.Name}:{failure.Failures[0].Type.ToString().ToLowerInvariant()}";
        var access = tool.IsReadOnly ? "read" : "write";
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, error),
            tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed,
                Error: $"Policy denied: {reason}").WithToolResultMessage(call),
            new AgentOperationResult(call.Name, call.Name, AgentRiskClass.Low, AgentConfirmationRequirement.None,
                AgentOperationStatus.Denied, PolicyReason: reason, Access: access),
            new AgentPolicyDenial(call.Name, call.Name, AgentRiskClass.Low, AgentConfirmationRequirement.None, reason),
            null,
            FailureKey: NormalizedToolCall.ErrorCode);
    }

    private static bool IsTargetDenialReason(string reason) =>
        reason.StartsWith("target_not_owned:", StringComparison.Ordinal)
        || reason.StartsWith(NormalizedToolCall.ErrorCode + ":", StringComparison.Ordinal);
}
