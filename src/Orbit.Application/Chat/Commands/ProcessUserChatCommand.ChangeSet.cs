namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task CombinePendingChangesAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (results.PendingOperations.Count < 2) return;
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var service = new AccountChangeSetService(execution.PendingOperationStore, previewer, ai.CatalogService, ai.ToolRegistry);
        var combined = await service.CombineAsync(request.UserId, results.PendingOperations, ct, request.ClientContext,
            conversation: request.PendingOperationConversation);
        if (combined is null) return;
        results.PendingOperations.RemoveAll(pending => combined.MemberIds.Contains(pending.Id));
        results.PendingOperations.Add(combined.PendingOperation);
        results.PendingWriteCalls[combined.PendingOperation.Id] = (AccountChangeSet.OperationName, combined.PendingOperation);
    }
}
