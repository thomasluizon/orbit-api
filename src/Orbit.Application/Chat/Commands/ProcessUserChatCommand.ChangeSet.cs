namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// Combines the previews that this turn prepared into one approval. An earlier preview that the turn only read stays
    /// its own card, so the person can still approve it alone.
    /// </summary>
    private async Task CombinePendingChangesAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        var prepared = results.Previews.Prepared.ToArray();
        if (prepared.Length < 2) return;
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var service = new AccountChangeSetService(execution.PendingOperationStore, previewer, ai.CatalogService, ai.ToolRegistry);
        var combined = await service.CombineAsync(request.UserId, prepared, ct, request.ClientContext,
            conversation: request.PendingOperationConversation);
        if (combined is null) return;
        results.PendingOperations.RemoveAll(pending => combined.MemberIds.Contains(pending.Id));
        results.PendingOperations.Add(combined.PendingOperation);
        results.PendingWriteCalls[combined.PendingOperation.Id] = (AccountChangeSet.OperationName, combined.PendingOperation);
        if (results.Recording?.Plan is { } plan)
            foreach (var step in plan.Steps.Where(step => step.PendingOperationId is { } id && combined.MemberIds.Contains(id)))
                await ChangePlanStepAsync(request, results, step.Id, "pending", pendingId: combined.PendingOperation.Id);
    }
}
