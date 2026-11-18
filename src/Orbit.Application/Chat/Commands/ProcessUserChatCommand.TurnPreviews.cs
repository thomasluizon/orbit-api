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
    /// round or token limit, a cancellation and any other exception. A turn that leaves part of the request withdraws its
    /// preview, and its answer says so. A turn that ends with an exception still decides, because a block answer replays
    /// its checkpoint, after it clears the changes that the exception left tracked, as the block answer recovery does.
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
        catch (Exception exception)
        {
            execution.UnitOfWork.ResetTracking();
            WithdrawPartialPreview(request.UserId, executionResults, language, failed: exception is not OperationCanceledException);
            throw;
        }
        WithdrawPartialPreview(request.UserId, executionResults, language, failed: false);
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
    /// A write that failed, that the ownership check turned down, or that a guard turned down and no prepared write of the
    /// turn carries (<see cref="CarriesRefusedWrite"/>), beside a change of the turn leaves part of the request. So does an
    /// exception other than a cancellation (<paramref name="failed"/>), and on the <c>ai_change_plan</c> path a last change
    /// list that failed. A question leaves a refusal to its answer. A plan, feature or policy denial does not withdraw.
    /// </summary>
    private bool LeavesPartOfTheRequest(ToolExecutionAccumulator results, bool failed) =>
        failed || (results.Policy.ChangePlan ? results.ChangesNotPrepared && results.AskBlock is null : results.Previews.HoldsChange
        && (results.OperationResults.Any(operation => IsOwnershipWriteDenial(operation)
                || operation.Status == AgentOperationStatus.Failed && operation.ErrorCode != ErrorCodes.HabitLimitReached
                && (operation.Access == "write" || ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }))
            || results.PendingSelection is null && results.AskBlock is null
            && results.RefusedWrites.Any(refused => !CarriesRefusedWrite(refused.Call, results))));

    /// <summary>
    /// A write that a guard of the handler turned down before dispatch, so it has no operation result: repeated single
    /// writes, unread duplicate targets, arguments that the handler rejected, or a restart plan that it rejected. A write
    /// that waits for the person's choice belongs to the selection guard, which asks for it.
    /// </summary>
    private bool IsRefusedWrite(AiToolCall call, ToolCallOutcome outcome) =>
        outcome is { OperationResult: null, ToolResult.Success: false } and not { FailureKey: SelectionUnansweredKey }
        && ai.ToolRegistry.GetTool(call.Name) is { IsReadOnly: false };

    /// <summary>
    /// The withdrawal decision of the turn. A turn that leaves part of the request withdraws what it prepared, and its
    /// answer says so.
    /// </summary>
    private void WithdrawPartialPreview(Guid userId, ToolExecutionAccumulator results, string? language, bool failed)
    {
        if (LeavesPartOfTheRequest(results, failed))
        {
            results.Previews.Withdraw(userId, execution.PendingOperationStore);
            results.SanitizeFailedActions(RequestNotPreparedMessage(results, language));
            results.RequestNotPrepared = true;
        }
    }

    /// <summary>
    /// The answer of a withdrawn turn. It says that nothing waits for approval only when no earlier preview still waits
    /// in the store after the withdrawal, whether the turn touched that preview or not.
    /// </summary>
    private static string RequestNotPreparedMessage(ToolExecutionAccumulator results, string? language) =>
        ErrorCopy.Resolve(results.Previews.KeepsEarlierPreview ? "chat.answer.requestNotPreparedEarlierKept" : "chat.answer.requestNotPrepared",
            LocaleHelper.IsPortuguese(language));

    /// <summary>A revision that removes every item of a preview discards it, through the preview ledger of the turn.</summary>
    private Task DiscardPreviewAsync(AiToolCall call, PendingPreviewDiscard discard, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, CancellationToken cancellationToken)
    {
        var tool = (RevisePendingChangesTool)ai.ToolRegistry.GetTool(call.Name)!;
        return results.Previews.DiscardAsync(discard.PendingOperationId,
            ct => tool.DiscardAsync(request.UserId, discard, request.ClientContext, ct), cancellationToken);
    }

    /// <summary>
    /// The previews of one turn. The ledger knows which previews were open before the turn ran a tool, keeps the stored
    /// state of each earlier preview that the turn revises, and decides how each close runs. A preview of this turn
    /// closes at once. An earlier preview closes when the person gets the previews of the turn (<see cref="SettleOnExitAsync"/>),
    /// so a withdrawn turn leaves it as it was. A close that the store refuses leaves the card in the pending set.
    /// </summary>
    internal sealed class TurnPreviewLedger(ToolExecutionAccumulator results, IReadOnlySet<Guid> earlierIds)
    {
        private readonly Dictionary<Guid, PendingAgentOperationRevision> _earlierRevisions = [];
        private readonly Dictionary<Guid, PendingAgentOperation[]> _combinedMembers = [];
        private readonly List<DeferredClose> _closes = [];

        /// <summary>
        /// The turn withdrew its change and an earlier preview still waits for the person's approval in the store, read
        /// after the withdrawal put back what the turn revised.
        /// </summary>
        public bool KeepsEarlierPreview { get; private set; }

        /// <summary>
        /// The turn changes something that a withdrawal takes back: a preview that it prepared, or an earlier preview
        /// that it discards, covers or replaces when it ends with its answer, or when a failed answer turn replays its
        /// checkpoint.
        /// </summary>
        public bool HoldsChange => Prepared.Any() || _closes.Count > 0;

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
        /// when the turn ends with its answer, or when a failed answer turn replays its checkpoint.
        /// </summary>
        public Task CloseAsync(Guid id, Func<CancellationToken, Task<bool>> close, CancellationToken ct) =>
            CloseAsync(id, close, ct, byRead: false);

        /// <summary>
        /// Closes a preview that a read call of the turn discards, as <see cref="CloseAsync"/> does. A failed change list
        /// of the same response does not take this close back, because the discard is not a write of that list and its
        /// result already went to the model.
        /// </summary>
        public Task DiscardAsync(Guid id, Func<CancellationToken, Task<bool>> close, CancellationToken ct) =>
            CloseAsync(id, close, ct, byRead: true);

        private async Task CloseAsync(Guid id, Func<CancellationToken, Task<bool>> close, CancellationToken ct, bool byRead)
        {
            if (earlierIds.Contains(id))
            {
                _closes.Add(new DeferredClose(id, results.PendingWriteCalls.TryGetValue(id, out var card) ? card : null, close, byRead));
                results.SupersedePendingOperation(id);
            }
            else if (await close(ct))
                results.SupersedePendingOperation(id);
        }

        /// <summary>
        /// Closes an earlier creation preview that a new preview of the turn replaces, when the turn ends with its
        /// answer, or when a failed answer turn replays its checkpoint. The turn finds it before any tool runs, so it
        /// has no card yet. A preview that a write of the turn shows again stays open.
        /// </summary>
        public void Replace(Guid id, Func<CancellationToken, Task<bool>> close)
        {
            if (_closes.All(item => item.Id != id))
                _closes.Add(new DeferredClose(id, null,
                    async ct => results.PendingOperations.All(pending => pending.Id != id) && await close(ct), ByRead: false));
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

        /// <summary>
        /// Keeps the previews of this turn that the change set <paramref name="changeSetId"/> combines, so a withdrawal
        /// can put back each earlier preview among them.
        /// </summary>
        public void KeepCombination(Guid changeSetId, IEnumerable<PendingAgentOperation> members) =>
            _combinedMembers[changeSetId] = [.. members];

        /// <summary>Keeps the state that <see cref="ReadRevisions"/> read for each revision of the round that prepared.</summary>
        public void KeepRevisions(IReadOnlyDictionary<string, (Guid Id, PendingAgentOperationRevision Revision)> revisions,
            IReadOnlyDictionary<string, ToolCallOutcome> outcomes)
        {
            foreach (var (callId, (id, revision)) in revisions)
                if (outcomes.TryGetValue(callId, out var outcome) && outcome.PendingOperation is not null)
                    _earlierRevisions.TryAdd(id, revision);
        }

        /// <summary>
        /// Withdraws only what the turn prepared, so it changes nothing that the person had. Each preview that a write of
        /// this turn created, also one in the answer checkpoint that a round stopped by an exception never recorded, is
        /// withdrawn in the store, so no approval, replay or later turn brings it back. Each earlier preview that this turn
        /// revised, also in a change set of the turn, gets its earlier state back; any other earlier preview stays as it was.
        /// </summary>
        public void Withdraw(Guid userId, IPendingAgentOperationStore store)
        {
            foreach (var close in _closes)
            {
                if (_earlierRevisions.ContainsKey(close.Id))
                    Restore(userId, store, close.Id, close.Card?.Preview.PreviewFingerprint);
                else if (close.Card is var (toolName, card))
                    results.KeepPendingOperation(toolName, card);
            }
            _closes.Clear();
            var checkpointed = results.CommittedPendingOperations.Where(pending => !earlierIds.Contains(pending.Id));
            foreach (var pending in Prepared.Concat(checkpointed).DistinctBy(pending => pending.Id).ToArray())
            {
                if (_earlierRevisions.ContainsKey(pending.Id))
                    Restore(userId, store, pending.Id, pending.PreviewFingerprint);
                else
                    WithdrawCreated(userId, store, pending.Id);
                results.SupersedePendingOperation(pending.Id);
            }
            KeepsEarlierPreview = earlierIds.Any(id => store.IsAwaitingConfirmation(userId, id));
        }

        /// <summary>The state of the turn before a change list runs, for <see cref="WithdrawList"/>.</summary>
        public ChangeListMark MarkList() => new(_closes.Count, [.. results.PreviewedCalls], results.PreviousWriteCalls.Count);

        /// <summary>
        /// Withdraws only what one failed change list prepared, so each preview of an earlier round stays. The store
        /// withdraws each preview that a write of the list created. A preview from before the turn stays in the store.
        /// Each close that a write added and each write guard after <paramref name="mark"/> is dropped, so the repair
        /// round can send the same change again. A close that a read call made, such as a discard, commits with the turn.
        /// </summary>
        public void WithdrawList(Guid userId, IPendingAgentOperationStore store, ChangeListMark mark,
            IReadOnlyCollection<Guid> listPreviewIds)
        {
            var surviving = new List<DeferredClose>();
            foreach (var close in _closes.Skip(mark.HeldCloses))
            {
                if (close.ByRead)
                    surviving.Add(close);
                else if (close.Card is var (toolName, card))
                    results.KeepPendingOperation(toolName, card);
            }
            _closes.RemoveRange(mark.HeldCloses, _closes.Count - mark.HeldCloses);
            _closes.AddRange(surviving);
            foreach (var id in listPreviewIds)
            {
                if (!earlierIds.Contains(id))
                {
                    WithdrawCreated(userId, store, id);
                    results.SupersedePendingOperation(id);
                }
                else if (!_earlierRevisions.ContainsKey(id))
                    results.PendingWriteCalls.Remove(id);
            }
            results.PreviewedCalls.IntersectWith(mark.PreviewedCalls);
            results.PreviousWriteCalls.RemoveRange(mark.WriteCalls, results.PreviousWriteCalls.Count - mark.WriteCalls);
        }

        /// <summary>
        /// Withdraws a preview that a write of this turn created. A change set of the turn first puts back each earlier
        /// preview that it combines and releases it, so the store withdraws only the members that this turn created.
        /// </summary>
        private void WithdrawCreated(Guid userId, IPendingAgentOperationStore store, Guid id)
        {
            var earlierMembers = _combinedMembers.TryGetValue(id, out var members)
                ? members.Where(member => _earlierRevisions.ContainsKey(member.Id)).ToArray() : null;
            foreach (var member in earlierMembers ?? [])
                Restore(userId, store, member.Id, member.PreviewFingerprint);
            if (store.GetExecution(userId, id) is { } stored)
                store.Withdraw(userId, id, AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()),
                    earlierMembers?.Select(member => member.Id).ToArray());
        }

        /// <summary>
        /// Closes the earlier previews of a turn that ends with its answer, or of a failed answer turn that replays its
        /// checkpoint. A preview that the store does not close stays in the answer when the turn showed it. Each close
        /// runs once, even when a later one throws.
        /// </summary>
        public async Task CommitAsync(CancellationToken ct)
        {
            while (_closes.Count > 0)
            {
                var close = _closes[0];
                _closes.RemoveAt(0);
                if (!await close.Close(ct) && close.Card is var (toolName, card))
                    results.KeepPendingOperation(toolName, card);
            }
        }

        /// <summary>
        /// Runs the answer of the turn and settles the turn on its way out. The answer path commits the held closes before
        /// it composes the reply. A failure result or an exception settles the turn as <see cref="SettleFailureAsync"/>
        /// says, after an exception once it clears the changes that the exception left tracked, as the block answer
        /// recovery does. An answer that shows no preview of the turn drops the held closes.
        /// </summary>
        public async Task<Result<ChatResponse>> SettleOnExitAsync(Func<Task<Result<ChatResponse>>> answer, Guid userId,
            IPendingAgentOperationStore store, IUnitOfWork unitOfWork)
        {
            try
            {
                var result = await answer();
                if (result.IsFailure)
                    await SettleFailureAsync(userId, store);
                return result;
            }
            catch
            {
                unitOfWork.ResetTracking();
                await SettleFailureAsync(userId, store);
                throw;
            }
            finally
            {
                _closes.Clear();
            }
        }

        /// <summary>
        /// Settles a turn that ends without its answer. No answer shows a change set of the turn, so each one is withdrawn
        /// and releases every member, which stays as the turn left it. The held closes commit when the turn saved an answer
        /// checkpoint, because the block answer replays it.
        /// </summary>
        private async Task SettleFailureAsync(Guid userId, IPendingAgentOperationStore store)
        {
            foreach (var (id, members) in _combinedMembers)
                if (store.GetExecution(userId, id) is { } stored)
                    store.Withdraw(userId, id, AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()),
                        [.. members.Select(member => member.Id)]);
            if (results.CommittedPendingOperations.Count > 0 || results.CommittedActions.Count > 0)
                await CommitAsync(CancellationToken.None);
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
        /// An earlier preview that this turn closes when it ends with its answer, or when a failed answer turn replays its
        /// checkpoint. <paramref name="Card"/> is its card in the answer of this turn, when a call of the turn returned it,
        /// and <paramref name="Close"/> closes it in the store. <paramref name="ByRead"/> is true when a read call of the turn
        /// made the close, and false when a write of the turn did.
        /// </summary>
        private sealed record DeferredClose(Guid Id, (string ToolName, PendingAgentOperation Preview)? Card,
            Func<CancellationToken, Task<bool>> Close, bool ByRead);
    }

    /// <summary>
    /// The state of the turn before a change list runs: the count of its held closes, the fingerprints of its previewed
    /// writes and the count of its write calls. A failed list goes back to it.
    /// </summary>
    internal sealed record ChangeListMark(int HeldCloses, IReadOnlyCollection<string> PreviewedCalls, int WriteCalls);
}
