using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ToolLoopResult> RunToolCallLoopAsync(
        AiResponse initialResponse,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        string? language,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        var aiResponse = HabitChangeClarification.CompleteText(initialResponse, request.Message, language);
        var iteration = 0;
        long totalReportedTokens = initialResponse.ReportedTokenCount;
        long totalBudgetTokens = initialResponse.RequestBudgetTokenCount ?? initialResponse.ReportedTokenCount;
        var unmetered = context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraEvalUnmetered, StringComparer.Ordinal);
        var tokenBudgetExceeded = !unmetered && totalBudgetTokens > AppConstants.MaxAiRequestTotalTokens;
        var hadToolFailure = false;
        var hasUnexecutedCalls = aiResponse.HasToolCalls;
        RejectedCallCorrection? correction = null;

        if (tokenBudgetExceeded)
        {
            LogAiRequestTokenCeilingReached(
                logger,
                totalReportedTokens,
                totalBudgetTokens,
                AppConstants.MaxAiRequestTotalTokens,
                iteration);
        }

        while (aiResponse.HasToolCalls && !aiResponse.IsTruncated
            && iteration < ChatTurnLimits.MaxToolIterations && !tokenBudgetExceeded)
        {
            iteration++;
            if (request.StreamSink is not null)
                await request.StreamSink(ChatStreamEvent.Round(iteration));

            hasUnexecutedCalls = false;
            var roundResult = await ProcessToolCallsAsync(
                aiResponse,
                request,
                context,
                executionResults,
                iteration,
                totalBudgetTokens,
                correction,
                language,
                aiStreamSink,
                cancellationToken);

            if (roundResult.ProviderError is not null)
                return new ToolLoopResult(aiResponse, iteration, tokenBudgetExceeded, hadToolFailure, roundResult.ProviderError);

            aiResponse = roundResult.Response;
            correction = roundResult.Correction;
            hasUnexecutedCalls = aiResponse.HasToolCalls;
            if (roundResult.HadToolFailure)
            {
                hadToolFailure = true;
                break;
            }

            totalReportedTokens += roundResult.Response.ReportedTokenCount;
            totalBudgetTokens += roundResult.Response.RequestBudgetTokenCount ?? roundResult.Response.ReportedTokenCount;
            tokenBudgetExceeded = !unmetered && totalBudgetTokens > AppConstants.MaxAiRequestTotalTokens;
            if (tokenBudgetExceeded)
            {
                LogAiRequestTokenCeilingReached(
                    logger,
                    totalReportedTokens,
                    totalBudgetTokens,
                    AppConstants.MaxAiRequestTotalTokens,
                    iteration);
            }
        }

        if (hasUnexecutedCalls && executionResults.EvalTrace is { } remainingTrace)
        {
            foreach (var call in aiResponse.ToolCalls!)
                remainingTrace.Record(call, null, null, ai.ToolRegistry.GetTool(call.Name) is IMcpOnlyAiTool);
        }

        if (correction is not null)
        {
            hadToolFailure = true;
            aiResponse = MessageResponse(language);
        }

        return new ToolLoopResult(
            aiResponse,
            iteration,
            tokenBudgetExceeded,
            hadToolFailure);
    }

    private static bool MustFailToolTurn(ProcessUserChatCommand request, ToolLoopResult result) =>
        result.ProviderError is not null || (request.BlockAnswer is not null && (result.HadToolFailure || result.FinalResponse.HasToolCalls));

    private bool HasSucceededWrite(ToolExecutionAccumulator results) =>
        results.OperationResults.Any(operation => operation.Status == AgentOperationStatus.Succeeded
            && ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false });

    private ToolLoopResult RecoverOutcomeContinuation(ProcessUserChatCommand request,
        ToolExecutionAccumulator results, ToolLoopResult loop, string? language, CancellationToken cancellationToken)
    {
        if (request.BlockAnswer is not null)
            return loop;
        if (loop.TokenBudgetExceeded && RecoverBudgetOutcome(request, results, language, cancellationToken) is { } recovery)
            return loop with { FinalResponse = recovery, ProviderError = null };
        if (loop.ProviderError is null)
            return loop;
        foreach (var pending in results.PendingOperations.ToArray())
            if (!IsCurrentPendingOutcome(request.UserId, pending, results))
                results.SupersedePendingOperation(pending.Id);
        if (results.PendingOperations.Count == 0
            && (loop.ProviderError.Code != ErrorCodes.AiEmptyResponse || !HasSucceededWrite(results)))
            return loop;
        return loop with { FinalResponse = new AiResponse(), ProviderError = null };
    }

    private async Task<Result<ChatResponse>> CompleteFailedToolTurnAsync(ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results, ToolLoopResult loop, CancellationToken ct)
    {
        using var phase = ChatTurnTimer.Measure(ChatPhase.Composition);
        ct.ThrowIfCancellationRequested();
        if (request.BlockAnswer is null && loop.ProviderError is { } error)
        {
            if (await RecoverExplicitHeadingsAsync(request, context, Result.Failure<AiResponse>(error), results, ct) is { } headings)
                return headings;
        }
        if (CanCompleteComposition(request, context, results))
            return Result.Success(CompleteCompositionWithoutNarrative(request, context, results));
        if (request.BlockAnswer is null && HasOnlySuccessfulRankingReads(results))
            return Result.Success(CompleteRankingWithoutNarrative(request, context, results));
        if (request.BlockAnswer is null && HasOnlySuccessfulFocusedReads(results))
            return Result.Success(CompleteFocusedReadsWithoutNarrative(request, context, results));
        return Result.Failure<ChatResponse>(loop.ProviderError ?? ErrorMessages.AiUnavailable);
    }

    private sealed record ToolLoopResult(
        AiResponse FinalResponse,
        int Iterations,
        bool TokenBudgetExceeded,
        bool HadToolFailure,
        AppError? ProviderError = null);

    /// <summary>
    /// Processes one iteration of AI tool calls: orders them, executes each, and sends results
    /// back to the AI. Returns the next AI response, or null if continuation failed.
    /// </summary>
    private async Task<ToolRoundResult> ProcessToolCallsAsync(
        AiResponse aiResponse,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        int iteration,
        long totalBudgetTokens,
        RejectedCallCorrection? correction,
        string? language,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        LogToolCallingIteration(logger, iteration, aiResponse.ToolCalls!.Count);

        var askCall = aiResponse.ToolCalls!.FirstOrDefault(call => call.Name == "ask");
        var orderedCalls = aiResponse.ToolCalls!
            .TakeWhile(call => call.Name != "ask")
            .Where(call => askCall is null || ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true)
            .OrderBy(c => ai.ToolRegistry.GetTool(c.Name)?.Order ?? int.MaxValue)
            .ToList();

        if (askCall is not null)
            orderedCalls.Add(askCall);
        orderedCalls = orderedCalls.Select(NormalizeExecutionArguments).ToList();
        var outcomesByCallId = correction is null
            ? await ExecuteToolCallsAsync(orderedCalls, request, executionResults, language, cancellationToken)
            : await ExecuteCorrectionsAsync(orderedCalls, correction, request, executionResults, cancellationToken);
        var toolResults = RecordToolRound(aiResponse, orderedCalls, outcomesByCallId, executionResults, correction is not null);
        executionResults.CapturePreviewDiscussionText(aiResponse.TextMessage);
        var hadToolFailure = outcomesByCallId.Values.Any(outcome => outcome.OperationResult?.Status == AgentOperationStatus.Failed);

        if (correction is not null)
            return correction.HasUnresolvedFailures
                ? new ToolRoundResult(MessageResponse(language), HadToolFailure: true)
                : new ToolRoundResult(new AiResponse());

        if (BulkNoChangesMessage(executionResults, language) is { } noChanges)
            return new ToolRoundResult(new AiResponse { TextMessage = noChanges });

        if (executionResults.AskBlock is not null)
            return new ToolRoundResult(aiResponse with { ToolCalls = null, ReportedTokenCount = 0, RequestBudgetTokenCount = 0 });

        if (CanCompleteReadRound(aiResponse, request, context, executionResults, iteration,
            orderedCalls, outcomesByCallId, toolResults))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ToolRoundResult(aiResponse with { ToolCalls = null, ReportedTokenCount = 0, RequestBudgetTokenCount = 0 });
        }

        if (hadToolFailure && !IsDeclaredTerminalReadRound(aiResponse, request, context, iteration))
        {
            if (await TryRecoverFreshCreateAsync(aiResponse, orderedCalls, outcomesByCallId, toolResults,
                request, context, executionResults, iteration, totalBudgetTokens, language, cancellationToken) is { } recovered)
                return recovered;
            if (!HasCorrectionAllowance(request, context, executionResults, iteration, totalBudgetTokens))
                return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);
            correction = new RejectedCallCorrection(executionResults.CorrectionOccurrences);
            OfferCorrections(toolResults, outcomesByCallId);
        }

        foreach (var skipped in aiResponse.ToolCalls!.Where(call => toolResults.All(result => result.Id != call.Id)))
            toolResults.Add(new AiToolCallResult(skipped.Name, skipped.Id, false, null, null,
                "The ask call ended this turn. Ask in plain text and make no further tool calls."));
        RetireConsumedReads(aiResponse.ConversationContext, executionResults, toolResults);
        cancellationToken.ThrowIfCancellationRequested();
        var continueResult = await ai.IntentService.ContinueWithToolResultsAsync(aiResponse.ConversationContext!, toolResults,
            correction is null ? aiStreamSink : null, cancellationToken);
        if (continueResult.IsFailure)
        {
            LogContinueWithToolResultsFailed(logger, continueResult.Error);
            if (correction is not null)
                return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);
            if (RecoverBudgetDeniedContinuation(continueResult.ErrorCode, request, executionResults, language, cancellationToken) is { } recovery)
                return new ToolRoundResult(recovery);
            return new ToolRoundResult(aiResponse, ProviderError: new AppError(
                continueResult.ErrorCode ?? ErrorMessages.AiUnavailable.Code, continueResult.Error));
        }

        return new ToolRoundResult(askCall is not null ? continueResult.Value with { ToolCalls = null } : continueResult.Value,
            HadToolFailure: hadToolFailure && correction is null,
            Correction: correction);
    }

    private void RetireConsumedReads(AiConversationContext? conversation, ToolExecutionAccumulator results,
        IReadOnlyList<AiToolCallResult> currentResults)
    {
        if (results.PendingOperations.Count == 0 || conversation is null) return;
        conversation.RetiredReadCallIds = results.ToolResults
            .Where(result => result.Success && currentResults.All(current => current.Id != result.Id)
                && ai.ToolRegistry.GetTool(result.Name)?.IsReadOnly == true)
            .Select(result => result.Id).ToArray();
    }

    private AiToolCall NormalizeExecutionArguments(AiToolCall call) => ai.ToolRegistry.GetTool(call.Name) is { } tool
        ? call with
        {
            Args = OptionalToolArguments.OmitOptionalNulls(
            tool.IsReadOnly ? TerminalReadDirective.ExecutionArguments(call.Args) : call.Args, tool.GetParameterSchema())
        }
        : call;

    private bool CanCompleteReadRound(AiResponse response, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, int iteration, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, IReadOnlyList<AiToolCallResult> toolResults) =>
        (IsDeclaredTerminalReadRound(response, request, context, iteration)
            || IsMissingHabitLookupRound(request, context, results, calls))
        && calls.Count == response.ToolCalls!.Count
        && outcomes.Count == calls.Count && toolResults.Count == calls.Count && toolResults.All(result => result.Success)
        && results.PendingOperations.Count == 0 && results.ActionResults.Count == 0
        && CanCompleteComposition(request, context, results);

    private static bool IsMissingHabitLookupRound(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, IReadOnlyList<AiToolCall> calls) => UsesComposition(request, context)
        && request.BlockAnswer is null && calls is [{ Name: "query_habits" }]
        && results.OperationResults is [{ ReadBlock: { } read }] && HabitReadMessages.IsMissingMetricsLookup(read);

    private bool IsDeclaredTerminalReadRound(AiResponse response, ProcessUserChatCommand request,
        ChatContext context, int iteration) =>
        iteration == 1 && UsesComposition(request, context) && request.BlockAnswer is null
        && TerminalReadDirective.IsComplete(response)
        && response.ToolCalls!.All(call => ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true);

    private AiResponse? RecoverBudgetDeniedContinuation(string? errorCode, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, string? language, CancellationToken cancellationToken)
    {
        if (errorCode != "AI_DAILY_BUDGET_EXCEEDED" || request.BlockAnswer is not null)
            return null;
        return RecoverBudgetOutcome(request, results, language, cancellationToken);
    }

    private AiResponse? RecoverBudgetOutcome(ProcessUserChatCommand request,
        ToolExecutionAccumulator results, string? language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasSavedActions = results.ActionResults.Any(action => action.Status == ActionStatus.Success
            && action.Params is not { Count: <= 0 } and not { Partial: true, Count: null }
            && results.OperationResults.Any(operation => operation.Status == AgentOperationStatus.Succeeded
                && ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }
                && ToolNameToPascalCase(operation.OperationId) == action.Type
                && operation.TargetId == action.EntityId?.ToString()));
        var invalidPending = results.PendingOperations.Where(pending =>
            !IsCurrentPendingOutcome(request.UserId, pending, results)).ToArray();
        if (!hasSavedActions && invalidPending.Length == results.PendingOperations.Count)
            return null;
        foreach (var pending in invalidPending)
            results.SupersedePendingOperation(pending.Id);
        results.HasBudgetRecovery = true;
        return new AiResponse { TextMessage = CommittedWriteMessage(language, hasSavedActions) };
    }

    private bool IsCurrentPendingOutcome(Guid userId, PendingAgentOperation pending, ToolExecutionAccumulator results)
    {
        if (pending.Items is not { Count: > 0 } || pending.ChangeTargetCount is not > 0
            || string.IsNullOrWhiteSpace(pending.PreviewFingerprint)
            || !results.PendingWriteCalls.TryGetValue(pending.Id, out var bound)
            || !execution.PendingOperationStore.IsAwaitingConfirmation(userId, pending.Id))
            return false;
        var stored = execution.PendingOperationStore.GetExecution(userId, pending.Id);
        return stored is { RequiresReproposal: false, Surface: AgentExecutionSurface.Chat }
            && stored.OperationId == bound.ToolName
            && stored.CapabilityId == pending.CapabilityId
            && stored.ConfirmationRequirement == pending.ConfirmationRequirement
            && stored.PreviewFingerprint == pending.PreviewFingerprint;
    }

    private List<AiToolCallResult> RecordToolRound(AiResponse response, IReadOnlyList<AiToolCall> orderedCalls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomesByCallId, ToolExecutionAccumulator executionResults,
        bool isCorrection)
    {
        var toolResults = new List<AiToolCallResult>(orderedCalls.Count);
        executionResults.CompositionRounds.Add(orderedCalls.Where(call => outcomesByCallId.ContainsKey(call.Id)).ToArray());
        if (executionResults.EvalTrace is { } evalTrace)
        {
            foreach (var call in response.ToolCalls!)
            {
                outcomesByCallId.TryGetValue(call.Id, out var outcome);
                evalTrace.Record(call, outcome?.ToolResult, outcome?.TraceStatus ?? outcome?.OperationResult?.Status,
                    ai.ToolRegistry.GetTool(call.Name) is IMcpOnlyAiTool);
            }
        }


        foreach (var call in orderedCalls)
        {
            if (!outcomesByCallId.TryGetValue(call.Id, out var outcome))
                continue;
            if (executionResults.Recording is { Enabled: true } recording)
                recording.ToolCalls.Add(new ConversationToolCall(call.Id, call.Name, call.Args.Clone(),
                    outcome.ToolResult.Success, outcome.ToolResult.Error));
            if (outcome.OperationResult?.ReadBlock is { } queryRead)
                executionResults.QueryReads[call.Id] = queryRead;
            toolResults.Add(outcome.ToolResult);
            executionResults.ToolResults.Add(outcome.ToolResult);
            executionResults.Add(
                call.Name,
                outcome.ActionResult,
                outcome.OperationResult,
                outcome.PolicyDenial,
                outcome.PendingOperation);
            if (!isCorrection && ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly != true)
                executionResults.CorrectionOccurrences.Add(new CorrectionOccurrence(call, outcome));
        }

        return toolResults;
    }

    private static bool HasCorrectionAllowance(ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results,
        int iteration, long totalBudgetTokens) =>
        iteration < ChatTurnLimits.MaxToolIterations
        && (context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraEvalUnmetered, StringComparer.Ordinal)
            || totalBudgetTokens < AppConstants.MaxAiRequestTotalTokens)
        && request.BlockAnswer is null && request.ConfirmationToken is null
        && RejectedCallCorrection.CanRecover(results.CorrectionOccurrences);

    private sealed record ToolRoundResult(
        AiResponse Response,
        bool HadToolFailure = false,
        AppError? ProviderError = null,
        RejectedCallCorrection? Correction = null);

    private static AiResponse MessageResponse(string? language) => new()
    {
        TextMessage = ToolFailureMessage(language)
    };

    private static string ToolFailureMessage(string? language) =>
        LocaleHelper.IsPortuguese(language)
            ? PortugueseToolFailureMessage
            : EnglishToolFailureMessage;

    private static string CommittedWriteMessage(string? language, bool hasSavedActions) =>
        LocaleHelper.IsPortuguese(language)
            ? hasSavedActions
                ? "Salvei as mudanças listadas aqui, mas não consegui terminar minha resposta."
                : "Não consegui terminar minha resposta, e nada muda até você aprovar."
            : hasSavedActions
                ? "I saved the changes listed here, but I couldn't finish my reply."
                : "I couldn't finish my reply, and nothing changes until you approve.";

    /// <summary>
    /// Executes a round's tool calls, dispatching the read-only subset concurrently (each on its
    /// own DI scope for DbContext isolation) and the write subset sequentially on the ambient
    /// scope in <c>Order</c>. Returns every outcome keyed by tool-call id so the caller can
    /// reassemble results deterministically, independent of task-completion timing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ToolCallOutcome>> ExecuteToolCallsAsync(
        List<AiToolCall> orderedCalls,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        string? language,
        CancellationToken cancellationToken)
    {
        var readOnlyCalls = orderedCalls
            .Where(call => call.Name != "ask" && ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true)
            .ToList();
        var writeCalls = orderedCalls
            .Where(call => call.Name != "ask" && ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly != true)
            .ToList();
        if (writeCalls.Count > 0 && !executionResults.HasMutationContext)
        {
            var habits = await data.HabitRepository.FindAsync(habit => habit.UserId == request.UserId,
                query => query.Include(habit => habit.Tags), cancellationToken);
            executionResults.AllHabits = habits;
            executionResults.Habits = habits.Where(habit => !habit.IsCompleted).ToList();
            executionResults.HasMutationContext = true;
        }
        var bulkRedirects = BulkToolRepeatGuard.FindRedirects(writeCalls, executionResults.PreviousWriteCalls);
        var resolvedWrites = writeCalls.ToDictionary(call => call.Id, call => ResolveHabitCall(call, executionResults));
        await CoalesceLogCallsAsync(writeCalls, resolvedWrites, request.UserId, executionResults, cancellationToken);
        CoalesceTagCalls(resolvedWrites, executionResults);
        PrepareHabitProposals(resolvedWrites, request, executionResults, language);

        var readOnlyTasks = readOnlyCalls
            .Select(call => ExecuteReadOnlyToolCallOnIsolatedScopeAsync(call, request, executionResults, cancellationToken))
            .ToList();
        var readOnlyOutcomes = await Task.WhenAll(readOnlyTasks);

        var outcomesByCallId = new Dictionary<string, ToolCallOutcome>(orderedCalls.Count, StringComparer.Ordinal);
        for (var index = 0; index < readOnlyCalls.Count; index++)
            outcomesByCallId[readOnlyCalls[index].Id] = readOnlyOutcomes[index];

        foreach (var call in writeCalls)
        {
            if (bulkRedirects.TryGetValue(call.Id, out var bulkTool))
            {
                outcomesByCallId[call.Id] = RepeatedSingleEntityToolOutcome(call, bulkTool);
                continue;
            }
            var resolved = resolvedWrites[call.Id];
            if (resolved is null || !executionResults.PreviewedCalls.Add(
                Orbit.Domain.Common.AgentOperationFingerprint.Compute(resolved.Name, resolved.Args.GetRawText())))
            {
                if (resolved is not null)
                    executionResults.MergedCallIds.Add(call.Id);
                outcomesByCallId[call.Id] = ResolvedWithoutWrite(call, executionResults, language);
                continue;
            }
            var outcome = executionResults.AnsweredAtUtc is { } claim
                ? await ExecuteBlockWriteAsync(resolved, request, executionResults, claim, cancellationToken)
                : await ExecuteSingleToolCallAsync(
                    resolved, request, executionResults, execution.OperationExecutor, execution.PendingClarificationStore, cancellationToken);
            if (outcome.ToolResult.Success)
                executionResults.PreviousWriteCalls.Add(call);
            else
                executionResults.PreviewedCalls.Remove(
                    Orbit.Domain.Common.AgentOperationFingerprint.Compute(resolved.Name, resolved.Args.GetRawText()));
            outcomesByCallId[call.Id] = outcome with { ToolResult = outcome.ToolResult with { Name = call.Name } };
        }

        if (orderedCalls.FirstOrDefault(call => call.Name == "ask") is { } askCall)
            outcomesByCallId[askCall.Id] = await ExecuteAskCallAsync(askCall, request, executionResults, cancellationToken);
        return outcomesByCallId;
    }

    private async Task<ToolCallOutcome> ExecuteBlockWriteAsync(AiToolCall call, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, DateTime claim, CancellationToken cancellationToken)
    {
        ToolCallOutcome? outcome = null;
        ActionResult[] actions = [];
        PendingAgentOperation[] pending = [];
        try
        {
            await execution.UnitOfWork.ExecuteOnceInTransactionAsync(async token =>
            {
                await execution.UnitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                var block = await execution.BlockStore.GetAsync(request.BlockAnswer!.BlockId, request.UserId, token);
                if (block?.AnsweredAtUtc != claim || block.ResponseJson is not null)
                    throw new InvalidOperationException("Block answer reservation was lost.");
                outcome = await ExecuteSingleToolCallAsync(call, request, results, execution.OperationExecutor,
                    execution.PendingClarificationStore, token);
                if (outcome.OperationResult?.Status is AgentOperationStatus.Failed or AgentOperationStatus.Denied
                    or AgentOperationStatus.UnsupportedByPolicy)
                    throw new BlockWriteRejectedException(outcome);
                actions = results.CommittedActions.Concat(outcome.ActionResult is { Status: ActionStatus.Success } action
                    ? new[] { action } : []).ToArray();
                pending = results.CommittedPendingOperations.Concat(outcome.PendingOperation is { } operation
                    ? new[] { operation } : []).ToArray();
                if (outcome.ActionResult is { Status: ActionStatus.Success } || outcome.PendingOperation is not null)
                {
                    using (ChatTurnTimer.Measure(ChatPhase.Save))
                        await execution.UnitOfWork.SaveChangesAsync(token);
                    var user = await data.UserRepository.GetByIdAsync(request.UserId, token);
                    var line = CommittedWriteMessage(ChatLocale.Resolve(request.ClientContext, user?.Language), actions.Length > 0);
                    var recovery = new ChatResponse(line, actions, PendingOperations: pending, CorrelationId: request.CorrelationId,
                        Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(line)] : null);
                    if (!await execution.BlockStore.CheckpointAnswerAsync(block.Id, request.UserId, claim,
                        JsonSerializer.Serialize(recovery, ChatStreamEvent.SerializerOptions), token))
                        throw new InvalidOperationException("Block answer reservation was lost.");
                }
            }, cancellationToken);
            results.CommittedActions.Clear();
            results.CommittedActions.AddRange(actions);
            results.CommittedPendingOperations.Clear();
            results.CommittedPendingOperations.AddRange(pending);
            return outcome ?? throw new InvalidOperationException("Tool execution did not complete.");
        }
        catch (BlockWriteRejectedException exception)
        {
            return exception.Outcome;
        }
    }

    private sealed class BlockWriteRejectedException(ToolCallOutcome outcome) : Exception
    {
        public ToolCallOutcome Outcome { get; } = outcome;
    }

    private static ToolCallOutcome RepeatedSingleEntityToolOutcome(AiToolCall call, string bulkTool)
    {
        var error = $"Repeated {call.Name} calls were blocked. Use one {bulkTool} call for the full matching set.";
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, error),
            new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: error).WithToolResultMessage(call, key: "repeatedSingleEntity", parameters: new(ToolName: call.Name, BulkTool: bulkTool)),
            null,
            null,
            null);
    }

    private async Task<ToolCallOutcome> ExecuteReadOnlyToolCallOnIsolatedScopeAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        CancellationToken cancellationToken)
    {
        using var scope = execution.ServiceScopeFactory.CreateScope();
        var scopedExecutor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();
        var scopedClarificationStore = scope.ServiceProvider.GetRequiredService<IPendingClarificationStore>();

        return await ExecuteSingleToolCallAsync(
            call, request, executionResults, scopedExecutor, scopedClarificationStore, cancellationToken);
    }

    /// <summary>
    /// Executes a single tool call: resolves the tool, runs it, and produces both a result
    /// for the AI and an optional action result for the frontend.
    /// </summary>
    private static AgentExecuteOperationResponse RevisedPreviewResponse(AiToolCall call, AgentExecuteOperationResponse response) =>
        call.Name == "revise_pending_changes" && response.Operation.Payload is PendingAgentOperation revised
            ? response with { Operation = response.Operation with { Status = AgentOperationStatus.PendingConfirmation, PendingOperationId = revised.Id }, PendingOperation = revised }
            : response;

    private async Task<ToolCallOutcome> ExecuteSingleToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        IAgentOperationExecutor operationExecutor,
        IPendingClarificationStore clarificationStore,
        CancellationToken cancellationToken,
        AgentExecuteOperationRequest? correctionRequest = null)
    {
        using var phase = ChatTurnTimer.Measure(ChatPhase.ToolCall);
        var tool = ai.ToolRegistry.GetTool(call.Name);
        if (tool is null)
        {
            LogUnknownToolRequested(logger, call.Name);
            return UnknownToolOutcome(call);
        }

        var capability = ai.CatalogService.GetCapabilityByChatTool(call.Name);
        if (capability is null)
            return UnsupportedByPolicyOutcome(call, tool);

        var access = tool.IsReadOnly ? "read" : "write";
        Func<Task>? onExecutionStarted = null;
        if (request.StreamSink is { } streamSink && request.ClientContext?.SupportsToolSteps == true)
        {
            onExecutionStarted = () => executionResults.EmitToolStepAsync(capability.Domain, access, streamSink);
        }

        var executionResponse = correctionRequest is null
            ? await DispatchToolCallAsync(call, request, operationExecutor, onExecutionStarted, cancellationToken)
            : await operationExecutor.ExecuteAsync(correctionRequest with { OnExecutionStarted = onExecutionStarted }, cancellationToken);
        executionResponse = RevisedPreviewResponse(call, executionResponse);
        RegisterPreviewDiscussion(call, executionResponse, executionResults);
        var operationResult = StatusCardBuilder.ForClient(executionResponse.Operation with { Access = access }, request.ClientContext);
        var toolResult = BuildToolCallResult(call, operationResult);
        LogToolCallOutcome(call, operationResult);

        RegisterPendingWrite(request.UserId, tool, executionResponse, executionResults);

        if (operationResult.Status == AgentOperationStatus.Succeeded
            && operationResult.Payload is NeedsClarificationPayload payload)
        {
            return await StashClarificationAsync(
                call, request, clarificationStore,
                new ClarificationToolResult(toolResult, operationResult, executionResponse, payload), cancellationToken);
        }

        return (operationResult.Status switch
        {
            AgentOperationStatus.PendingConfirmation => new ToolCallOutcome(
                new AiToolCallResult(call.Name, call.Id, true, null, null, null, new
                {
                    status = "awaiting_confirmation",
                    item_count = executionResponse.PendingOperation?.ChangeTargetCount,
                    habit_count = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?.Select(item => item.EntityId).Distinct().Count() : (int?)null,
                    date_count = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?.SelectMany(item => item.Fields).Where(field => field.Field == "date").Select(field => field.NewValue).Distinct().Count() : (int?)null,
                    proposed_titles = executionResponse.PendingOperation?.Items?.Select(item => item.EntityName).Distinct().Take(10).ToArray(),
                    preview_items = executionResponse.PendingOperation?.Items,
                    dates = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?
                        .SelectMany(item => item.Fields).Where(field => field.Field == "date")
                        .Select(field => field.NewValue).Distinct().Order().ToArray() : null,
                    instruction = executionResults.IsPreviewDiscussion
                        ? "Answer the person's question about the preview in one short sentence without figures. The preview remains pending beside the answer. Do not apply or revise anything."
                        : "The preview is the entire answer. Emit no prose before or after it. All preview facts come exclusively from preview_items."
                }),
                null,
                null,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation,
                AgentOperationStatus.PendingConfirmation),
            AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy => new ToolCallOutcome(
                toolResult,
                tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: toolResult.Error).WithToolResultMessage(call),
                operationResult,
                executionResponse.PolicyDenial,
                null),
            _ => new ToolCallOutcome(
                toolResult,
                BuildActionResult(call, tool, ToToolResult(operationResult)),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation)
        }) with
        { PreparedResponse = executionResponse as PreparedToolOperationResponse };
    }

    private static void RegisterPreviewDiscussion(AiToolCall call, AgentExecuteOperationResponse response, ToolExecutionAccumulator results)
    {
        if (call.Name == "revise_pending_changes" && response.PendingOperation is not null
            && (Orbit.Application.Chat.Tools.Implementations.RevisePendingChangesTool.IsInspection(call.Args)
                || JsonArgumentParser.GetOptionalBool(call.Args, "answer_question") == true))
            results.IsPreviewDiscussion = true;
    }

    private void RegisterPendingWrite(Guid userId, IAiTool tool,
        AgentExecuteOperationResponse response, ToolExecutionAccumulator results)
    {
        if (response.Operation.Status != AgentOperationStatus.PendingConfirmation
            || response.PendingOperation is not { } replacement)
            return;
        if (tool.Name == "revise_pending_changes")
        {
            if (execution.PendingOperationStore.GetExecution(userId, replacement.Id) is { } stored)
                results.PendingWriteCalls[replacement.Id] = (stored.OperationId, replacement);
            return;
        }
        if (tool.IsReadOnly)
            return;
        foreach (var (id, previous) in results.PendingWriteCalls.ToArray())
        {
            if (id == replacement.Id || !BulkToolRepeatGuard.IsBulkAlternative(previous.ToolName, tool.Name)
                || !CoversPendingChanges(replacement, previous.Preview))
                continue;
            var stored = execution.PendingOperationStore.GetExecution(userId, id);
            if (stored is null)
            {
                results.SupersedePendingOperation(id);
                continue;
            }
            if (stored.PreviewFingerprint != previous.Preview.PreviewFingerprint)
                continue;
            var fingerprint = AgentOperationFingerprint.Compute(
                stored.OperationId, stored.Arguments.GetRawText());
            if (execution.PendingOperationStore.Cancel(userId, id, fingerprint))
                results.SupersedePendingOperation(id);
        }
        results.PendingWriteCalls[replacement.Id] = (tool.Name, replacement);
    }

    private static bool CoversPendingChanges(PendingAgentOperation replacement, PendingAgentOperation previous)
    {
        if (replacement.Items is not { Count: > 0 } || previous.Items is not { Count: > 0 })
            return false;
        return previous.Items.All(item =>
        {
            var fields = item.Fields.Where(field => field.Field != "habit_id").ToArray();
            return item.EntityId is { } entityId && fields.Length > 0
                && replacement.Items.Any(candidate => candidate.EntityId == entityId
                    && fields.All(field => candidate.Fields.Any(proposed => proposed.Field == field.Field
                        && SameProposedValue(field, proposed))));
        });
    }

    private static bool SameProposedValue(PendingOperationChange previous, PendingOperationChange replacement) =>
        previous.ProposedValue is { } oldValue && replacement.ProposedValue is { } newValue
            ? AgentOperationFingerprint.Canonicalize(oldValue.GetRawText())
                == AgentOperationFingerprint.Canonicalize(newValue.GetRawText())
            : replacement.NewValue == previous.NewValue;

    private ToolCallOutcome UnknownToolOutcome(AiToolCall call)
    {
        var capability = ai.CatalogService.GetCapabilityByChatTool(call.Name);
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, $"Unknown tool: {call.Name}"),
            new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: $"Unknown tool: {call.Name}").WithToolResultMessage(call, key: "unknownTool", parameters: new(ToolName: call.Name)),
            new AgentOperationResult(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                PolicyReason: UnsupportedByPolicyReason,
                Access: capability is null ? null : capability.IsMutation ? "write" : "read"),
            new AgentPolicyDenial(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                UnsupportedByPolicyReason),
            null);
    }

    private static ToolCallOutcome UnsupportedByPolicyOutcome(AiToolCall call, IAiTool tool)
    {
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, "Operation is unsupported by policy."),
            tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: "Operation is unsupported by policy.").WithToolResultMessage(call),
            new AgentOperationResult(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                Summary: BuildOperationSummary(call),
                PolicyReason: UnsupportedByPolicyReason,
                Access: tool.IsReadOnly ? "read" : "write"),
            new AgentPolicyDenial(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                UnsupportedByPolicyReason),
            null);
    }

    private static async Task<AgentExecuteOperationResponse> DispatchToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        IAgentOperationExecutor operationExecutor,
        Func<Task>? onExecutionStarted,
        CancellationToken cancellationToken)
    {
        var dispatchArgs = call.Name == "send_support_request" && !string.IsNullOrWhiteSpace(request.CorrelationId)
            ? AppendSupportTrace(call.Args, request.CorrelationId)
            : call.Args;

        return await operationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            request.UserId,
            call.Name,
            dispatchArgs,
            AgentExecutionSurface.Chat,
            request.AuthMethod,
            request.GrantedScopes,
            request.IsReadOnlyCredential,
            request.ConfirmationToken,
            request.CorrelationId,
            IncludeChangePreview: request.ClientContext?.SupportsPendingOperationChanges == true,
            OnExecutionStarted: onExecutionStarted,
            ClientContext: request.ClientContext), cancellationToken);
    }

    private void LogToolCallOutcome(AiToolCall call, AgentOperationResult operationResult)
    {
        var isClarification = operationResult.Payload is NeedsClarificationPayload;

        if (operationResult.Status == AgentOperationStatus.Succeeded && !isClarification)
        {
            LogToolSucceeded(logger, call.Name, operationResult.TargetName);
        }
        else if (operationResult.Status is AgentOperationStatus.Failed or AgentOperationStatus.Denied)
        {
            LogToolFailed(logger, call.Name, operationResult.ErrorCode ?? operationResult.Status.ToString());
            if (isClarification)
                LogClarificationDroppedOnFailedTool(logger, call.Name, operationResult.PolicyReason);
        }
    }

    private sealed record ClarificationToolResult(
        AiToolCallResult ToolResult,
        AgentOperationResult OperationResult,
        AgentExecuteOperationResponse ExecutionResponse,
        NeedsClarificationPayload Payload);

    private async Task<ToolCallOutcome> StashClarificationAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        IPendingClarificationStore clarificationStore,
        ClarificationToolResult toolCallResult,
        CancellationToken cancellationToken)
    {
        var (toolResult, operationResult, executionResponse, payload) = toolCallResult;
        var quickActionsJson = payload.QuickActions is null
            ? "[]"
            : JsonSerializer.Serialize(payload.QuickActions);

        var partialArgsJson = call.Args.GetRawText();
        if (partialArgsJson.Length > AppConstants.MaxClarificationArgsLength)
        {
            LogClarificationArgsTooLarge(logger, call.Name, partialArgsJson.Length);
            return new ToolCallOutcome(
                toolResult,
                new ActionResult(
                    ToolNameToPascalCase(call.Name),
                    ActionStatus.Failed,
                    Error: "Tool arguments exceeded the clarification stash limit.").WithToolResultMessage(call),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation);
        }

        var stashedId = await clarificationStore.CreateAsync(
            request.UserId,
            call.Name,
            partialArgsJson,
            payload.MissingArgumentKey,
            payload.Question,
            quickActionsJson,
            cancellationToken);
        LogClarificationRequested(logger, call.Name, stashedId, payload.MissingArgumentKey);
        var clarification = new ClarificationRequest(
            payload.Question,
            stashedId,
            payload.MissingArgumentKey,
            payload.QuickActions ?? Array.Empty<QuickAction>());
        return new ToolCallOutcome(
            toolResult,
            new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.NeedsClarification,
                EntityName: call.Name,
                ClarificationRequest: clarification).WithToolResultMessage(call),
            operationResult,
            executionResponse.PolicyDenial,
            executionResponse.PendingOperation);
    }

    internal sealed record ToolCallOutcome(
        AiToolCallResult ToolResult,
        ActionResult? ActionResult,
        AgentOperationResult? OperationResult,
        AgentPolicyDenial? PolicyDenial,
        PendingAgentOperation? PendingOperation,
        AgentOperationStatus? TraceStatus = null,
        PreparedToolOperationResponse? PreparedResponse = null);
}
