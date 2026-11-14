namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// Combines the previews that this turn prepared into one approval. An earlier preview that the turn only read stays
    /// its own card, so the person can still approve it alone. A combine that throws, as when the store refuses to bind
    /// the change set, first withdraws the previews of the turn as a round that throws does, so each earlier preview that
    /// the turn revised gets its state from before the turn back.
    /// </summary>
    private async Task CombinePendingChangesAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        var prepared = results.Previews.Prepared.ToArray();
        if (prepared.Length < 2) return;
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var service = new AccountChangeSetService(execution.PendingOperationStore, previewer, ai.CatalogService, ai.ToolRegistry);
        PendingAccountChangeSet? combined;
        try
        {
            combined = await service.CombineAsync(request.UserId, prepared, ct, request.ClientContext,
                conversation: request.PendingOperationConversation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.UnitOfWork.ResetTracking();
            results.Previews.Withdraw(request.UserId, execution.PendingOperationStore);
            throw;
        }
        if (combined is null) return;
        results.Previews.KeepCombination(combined.PendingOperation.Id, prepared.Where(pending => combined.MemberIds.Contains(pending.Id)));
        results.PendingOperations.RemoveAll(pending => combined.MemberIds.Contains(pending.Id));
        results.PendingOperations.Add(combined.PendingOperation);
        results.PendingWriteCalls[combined.PendingOperation.Id] = (AccountChangeSet.OperationName, combined.PendingOperation);
        if (results.Recording?.Plan is { } plan)
            foreach (var step in plan.Steps.Where(step => step.PendingOperationId is { } id && combined.MemberIds.Contains(id)))
                await ChangePlanStepAsync(request, results, step.Id, "pending", pendingId: combined.PendingOperation.Id);
    }
}
