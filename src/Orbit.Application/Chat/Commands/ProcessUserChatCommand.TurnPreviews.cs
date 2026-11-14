using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// Runs the tool rounds and decides the withdrawal once for every way out of them: a normal end, a provider error, the
    /// round or token limit, and a cancellation. A turn that leaves part of the request withdraws its preview, and its
    /// answer says so whatever the last round returned. A cancelled turn has no answer of its own, but it still closes
    /// its preview, because a block answer replays its checkpoint.
    /// </summary>
    private async Task<ToolLoopResult> RunToolCallLoopAsync(
        AiResponse initialResponse,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        string? language,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        ToolLoopResult loop;
        try
        {
            loop = await RunToolRoundsAsync(initialResponse, request, context, executionResults, language, aiStreamSink, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WithdrawPartialPreview(request.UserId, executionResults, language);
            throw;
        }
        WithdrawPartialPreview(request.UserId, executionResults, language);
        return executionResults.RequestNotPrepared
            ? loop with
            {
                FinalResponse = new AiResponse { TextMessage = RequestNotPreparedMessage(executionResults, language) },
                HadToolFailure = false,
                ProviderError = null
            }
            : loop;
    }

    /// <summary>
    /// A write that failed, or that the ownership check turned down, beside a prepared write leaves a part of the
    /// request that a second send can prepare, so the turn holds no preview and says so. A plan, feature or policy
    /// denial does not change on a second send: the turn keeps the prepared part and the reason for the denial.
    /// </summary>
    private bool LeavesPartOfTheRequest(ToolExecutionAccumulator results) =>
        results.Previews.Prepared.Any()
        && results.OperationResults.Any(operation => IsOwnershipWriteDenial(operation)
            || operation.Status == AgentOperationStatus.Failed && operation.ErrorCode != ErrorCodes.HabitLimitReached
            && (operation.Access == "write" || ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }));

    /// <summary>A turn that leaves part of the request withdraws what it prepared, and its answer says so.</summary>
    private void WithdrawPartialPreview(Guid userId, ToolExecutionAccumulator results, string? language)
    {
        if (!LeavesPartOfTheRequest(results))
            return;
        results.Previews.Withdraw(userId, execution.PendingOperationStore);
        results.SanitizeFailedActions(RequestNotPreparedMessage(results, language));
        results.RequestNotPrepared = true;
    }

    /// <summary>
    /// The answer of a withdrawn turn. It says that nothing waits for approval only when no earlier preview that the
    /// turn read or revised still waits.
    /// </summary>
    private static string RequestNotPreparedMessage(ToolExecutionAccumulator results, string? language) =>
        ErrorCopy.Resolve(results.Previews.KeepsEarlierPreview ? "chat.answer.requestNotPreparedEarlierKept" : "chat.answer.requestNotPrepared",
            LocaleHelper.IsPortuguese(language));

    /// <summary>A revision that removes every item of a preview discards it, through the preview ledger of the turn.</summary>
    private Task DiscardPreviewAsync(AiToolCall call, PendingPreviewDiscard discard, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, CancellationToken cancellationToken)
    {
        var tool = (RevisePendingChangesTool)ai.ToolRegistry.GetTool(call.Name)!;
        return results.Previews.CloseAsync(discard.PendingOperationId,
            ct => tool.DiscardAsync(request.UserId, discard, request.ClientContext, ct), cancellationToken);
    }

    /// <summary>
    /// The previews of one turn. The ledger knows which previews were open before the turn ran a tool, keeps the stored
    /// state of each earlier preview that the turn revises, and decides how each close runs. A preview of this turn
    /// closes at once. An earlier preview closes when the turn ends with its answer, so a withdrawn turn leaves it as it
    /// was. A close that the store refuses leaves the card in the pending set.
    /// </summary>
    internal sealed class TurnPreviewLedger(ToolExecutionAccumulator results, IReadOnlySet<Guid> earlierIds)
    {
        private readonly Dictionary<Guid, PendingAgentOperationRevision> _earlierRevisions = [];
        private readonly List<DeferredClose> _closes = [];

        /// <summary>The turn withdrew its preview and an earlier preview still waits for the person's approval.</summary>
        public bool KeepsEarlierPreview { get; private set; }

        /// <summary>
        /// The previews that this turn prepared: each one that its writes created, and each earlier one that it
        /// revised. An earlier preview that the turn only read, or that a write found already waiting, stays the
        /// person's earlier card and is never a prepared write of this turn.
        /// </summary>
        public IEnumerable<PendingAgentOperation> Prepared => results.PendingOperations.Where(pending =>
            !earlierIds.Contains(pending.Id) || _earlierRevisions.ContainsKey(pending.Id));

        /// <summary>
        /// Closes a preview that a write or a revision of the turn replaced or emptied. A preview of this turn closes
        /// at once and leaves the answer when the store closes it. An earlier preview leaves the answer now and closes
        /// when the turn ends with its answer.
        /// </summary>
        public async Task CloseAsync(Guid id, Func<CancellationToken, Task<bool>> close, CancellationToken ct)
        {
            if (earlierIds.Contains(id))
            {
                _closes.Add(new DeferredClose(id, results.PendingWriteCalls.TryGetValue(id, out var card) ? card : null, close));
                results.SupersedePendingOperation(id);
            }
            else if (await close(ct))
                results.SupersedePendingOperation(id);
        }

        /// <summary>
        /// Closes an earlier creation preview that a new preview of the turn replaces, when the turn ends with its
        /// answer. The turn finds it before any tool runs, so it has no card yet. A preview that a write of the turn
        /// shows again stays open.
        /// </summary>
        public void Replace(Guid id, Func<CancellationToken, Task<bool>> close)
        {
            if (_closes.All(item => item.Id != id))
                _closes.Add(new DeferredClose(id, null,
                    async ct => results.PendingOperations.All(pending => pending.Id != id) && await close(ct)));
        }

        /// <summary>
        /// Reads the stored state of each earlier preview that a revision in this round changes, before the revision
        /// runs, so a turn that ends without its whole request can put the preview back.
        /// </summary>
        public Dictionary<string, (Guid Id, PendingAgentOperationRevision Revision)> ReadRevisions(
            IEnumerable<AiToolCall> calls, Guid userId, IPendingAgentOperationStore store)
        {
            var revisions = new Dictionary<string, (Guid, PendingAgentOperationRevision)>(StringComparer.Ordinal);
            foreach (var call in calls.Where(call => call.Name == "revise_pending_changes" && !RevisePendingChangesTool.IsInspection(call.Args)))
                if (Guid.TryParse(JsonArgumentParser.GetOptionalString(call.Args, "pending_operation_id"), out var id)
                    && earlierIds.Contains(id) && !_earlierRevisions.ContainsKey(id)
                    && store.GetRevision(userId, id) is { } revision)
                    revisions[call.Id] = (id, revision);
            return revisions;
        }

        /// <summary>Keeps the state that <see cref="ReadRevisions"/> read for each revision of the round that prepared.</summary>
        public void KeepRevisions(IReadOnlyDictionary<string, (Guid Id, PendingAgentOperationRevision Revision)> revisions,
            IReadOnlyDictionary<string, ToolCallOutcome> outcomes)
        {
            foreach (var (callId, (id, revision)) in revisions)
                if (outcomes.TryGetValue(callId, out var outcome) && outcome.PendingOperation is not null)
                    _earlierRevisions.TryAdd(id, revision);
        }

        /// <summary>
        /// Withdraws only what the turn prepared, so it changes nothing that the person had. A preview that a write of
        /// this turn created is closed in the pending operation store too, so no approval, no replay of the answer
        /// checkpoint and no later turn brings it back. An earlier preview that this turn revised gets its earlier
        /// state back. An earlier preview that this turn read, or was to close at its end, stays as it was.
        /// </summary>
        public void Withdraw(Guid userId, IPendingAgentOperationStore store)
        {
            KeepsEarlierPreview = _closes.Count > 0 || results.PendingOperations.Any(pending => earlierIds.Contains(pending.Id));
            foreach (var close in _closes)
            {
                if (_earlierRevisions.ContainsKey(close.Id))
                    Restore(userId, store, close.Id, close.Card?.Preview.PreviewFingerprint);
                else if (close.Card is var (toolName, card))
                    results.KeepPendingOperation(toolName, card);
            }
            _closes.Clear();
            foreach (var pending in Prepared.ToArray())
            {
                if (_earlierRevisions.ContainsKey(pending.Id))
                    Restore(userId, store, pending.Id, pending.PreviewFingerprint);
                else if (store.GetExecution(userId, pending.Id) is { } stored)
                    store.Cancel(userId, pending.Id, AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()));
                results.SupersedePendingOperation(pending.Id);
            }
        }

        /// <summary>
        /// Closes the earlier previews of a turn that ends with its answer. A preview that the store does not close
        /// stays in the answer when the turn showed it.
        /// </summary>
        public async Task CommitAsync(CancellationToken ct)
        {
            foreach (var close in _closes)
                if (!await close.Close(ct) && close.Card is var (toolName, card))
                    results.KeepPendingOperation(toolName, card);
            _closes.Clear();
        }

        /// <summary>
        /// Puts back the state from before this turn's revision of an earlier preview, while the store still holds the
        /// preview of that revision.
        /// </summary>
        private void Restore(Guid userId, IPendingAgentOperationStore store, Guid id, string? revisedFingerprint)
        {
            if (store.GetExecution(userId, id) is { } stored && stored.PreviewFingerprint == revisedFingerprint)
                store.Restore(userId, id,
                    AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()), _earlierRevisions[id]);
        }

        /// <summary>
        /// An earlier preview that this turn closes when it ends with its answer. <paramref name="Card"/> is its card in
        /// the answer of this turn, when a call of the turn returned it, and <paramref name="Close"/> closes it in the store.
        /// </summary>
        private sealed record DeferredClose(Guid Id, (string ToolName, PendingAgentOperation Preview)? Card,
            Func<CancellationToken, Task<bool>> Close);
    }
}
