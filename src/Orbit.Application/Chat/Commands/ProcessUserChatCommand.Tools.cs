using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ToolLoopResult> RunToolCallLoopAsync(
        AiResponse initialResponse,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        string? language,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        var aiResponse = initialResponse;
        var iteration = 0;
        long totalReportedTokens = initialResponse.ReportedTokenCount;
        var tokenBudgetExceeded = totalReportedTokens > AppConstants.MaxAiRequestTotalTokens;
        var hadToolFailure = false;

        if (tokenBudgetExceeded)
        {
            LogAiRequestTokenCeilingReached(
                logger,
                totalReportedTokens,
                AppConstants.MaxAiRequestTotalTokens,
                iteration);
        }

        while (aiResponse.HasToolCalls && iteration < ChatTurnLimits.MaxToolIterations && !tokenBudgetExceeded)
        {
            iteration++;
            if (request.StreamSink is not null)
                await request.StreamSink(ChatStreamEvent.Round(iteration));

            var roundResult = await ProcessToolCallsAsync(
                aiResponse,
                request,
                executionResults,
                iteration,
                language,
                aiStreamSink,
                cancellationToken);

            if (roundResult is null)
                break;

            aiResponse = roundResult.Response;
            if (roundResult.HadToolFailure)
            {
                hadToolFailure = true;
                break;
            }

            totalReportedTokens += roundResult.Response.ReportedTokenCount;
            tokenBudgetExceeded = totalReportedTokens > AppConstants.MaxAiRequestTotalTokens;
            if (tokenBudgetExceeded)
            {
                LogAiRequestTokenCeilingReached(
                    logger,
                    totalReportedTokens,
                    AppConstants.MaxAiRequestTotalTokens,
                    iteration);
            }
        }

        return new ToolLoopResult(
            aiResponse,
            iteration,
            tokenBudgetExceeded,
            hadToolFailure);
    }

    private sealed record ToolLoopResult(
        AiResponse FinalResponse,
        int Iterations,
        bool TokenBudgetExceeded,
        bool HadToolFailure);

    /// <summary>
    /// Processes one iteration of AI tool calls: orders them, executes each, and sends results
    /// back to the AI. Returns the next AI response, or null if continuation failed.
    /// </summary>
    private async Task<ToolRoundResult?> ProcessToolCallsAsync(
        AiResponse aiResponse,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        int iteration,
        string? language,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        LogToolCallingIteration(logger, iteration, aiResponse.ToolCalls!.Count);

        var askCall = aiResponse.ToolCalls!.FirstOrDefault(call => call.Name == "ask");
        var orderedCalls = aiResponse.ToolCalls!
            .TakeWhile(call => call.Name != "ask")
            .OrderBy(c => ai.ToolRegistry.GetTool(c.Name)?.Order ?? int.MaxValue)
            .ToList();

        if (askCall is not null)
            orderedCalls.Add(askCall);
        var outcomesByCallId = await ExecuteToolCallsAsync(orderedCalls, request, executionResults, cancellationToken);

        var toolResults = new List<AiToolCallResult>(orderedCalls.Count);
        var hadToolFailure = false;
        foreach (var call in orderedCalls)
        {
            var outcome = outcomesByCallId[call.Id];
            toolResults.Add(outcome.ToolResult);
            executionResults.ToolResults.Add(outcome.ToolResult);
            executionResults.Add(
                call.Name,
                outcome.ActionResult,
                outcome.OperationResult,
                outcome.PolicyDenial,
                outcome.PendingOperation);

            if (outcome.OperationResult?.Status == AgentOperationStatus.Failed)
                hadToolFailure = true;
        }

        if (executionResults.AskBlock is not null)
            return new ToolRoundResult(aiResponse with { ToolCalls = null, ReportedTokenCount = 0 });

        if (hadToolFailure)
            return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);

        foreach (var skipped in aiResponse.ToolCalls!.Where(call => toolResults.All(result => result.Id != call.Id)))
            toolResults.Add(new AiToolCallResult(skipped.Name, skipped.Id, false, null, null,
                "The ask call ended this turn. Ask in plain text and make no further tool calls."));
        var continueResult = await ai.IntentService.ContinueWithToolResultsAsync(aiResponse.ConversationContext!, toolResults, aiStreamSink, cancellationToken);
        if (continueResult.IsFailure)
        {
            LogContinueWithToolResultsFailed(logger, continueResult.Error);
            return null;
        }

        return new ToolRoundResult(askCall is not null ? continueResult.Value with { ToolCalls = null } : continueResult.Value);
    }

    private sealed record ToolRoundResult(
        AiResponse Response,
        bool HadToolFailure = false);

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
        CancellationToken cancellationToken)
    {
        var readOnlyCalls = orderedCalls
            .Where(call => call.Name != "ask" && ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true)
            .ToList();
        var writeCalls = orderedCalls
            .Where(call => call.Name != "ask" && ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly != true)
            .ToList();
        var bulkRedirects = BulkToolRepeatGuard.FindRedirects(writeCalls);
        var resolvedWrites = writeCalls.ToDictionary(call => call.Id, call => ResolveHabitCall(call, executionResults));
        await CoalesceLogCallsAsync(writeCalls, resolvedWrites, request.UserId, executionResults, cancellationToken);
        CoalesceTagCalls(resolvedWrites);

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
                outcomesByCallId[call.Id] = ResolvedWithoutWrite(call, executionResults);
                continue;
            }
            var outcome = executionResults.AnsweredAtUtc is { } claim
                ? await ExecuteBlockWriteAsync(resolved, request, executionResults, claim, cancellationToken)
                : await ExecuteSingleToolCallAsync(
                    resolved, request, executionResults, execution.OperationExecutor, execution.PendingClarificationStore, cancellationToken);
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
                    await execution.UnitOfWork.SaveChangesAsync(token);
                    var user = await data.UserRepository.GetByIdAsync(request.UserId, token);
                    var line = CommittedWriteMessage(GetUserLanguage(user), actions.Length > 0);
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
            new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: error),
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
    private async Task<ToolCallOutcome> ExecuteSingleToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        IAgentOperationExecutor operationExecutor,
        IPendingClarificationStore clarificationStore,
        CancellationToken cancellationToken)
    {
        var tool = ai.ToolRegistry.GetTool(call.Name);
        if (tool is null)
        {
            LogUnknownToolRequested(logger, call.Name);
            return UnknownToolOutcome(call);
        }

        var capability = ai.CatalogService.GetCapabilityByChatTool(call.Name);
        if (capability is null)
            return UnsupportedByPolicyOutcome(call, tool);

        Func<Task>? onExecutionStarted = null;
        if (request.StreamSink is { } streamSink && request.ClientContext?.SupportsToolSteps == true)
        {
            var access = tool.IsReadOnly ? "read" : "write";
            onExecutionStarted = () => executionResults.EmitToolStepAsync(capability.Domain, access, streamSink);
        }

        var executionResponse = await DispatchToolCallAsync(call, request, operationExecutor, onExecutionStarted, cancellationToken);
        var operationResult = executionResponse.Operation;
        var toolResult = BuildToolCallResult(call, operationResult);
        LogToolCallOutcome(call, operationResult);

        if (operationResult.Status == AgentOperationStatus.Succeeded
            && operationResult.Payload is NeedsClarificationPayload payload)
        {
            return await StashClarificationAsync(
                call, request, clarificationStore,
                new ClarificationToolResult(toolResult, operationResult, executionResponse, payload), cancellationToken);
        }

        return operationResult.Status switch
        {
            AgentOperationStatus.PendingConfirmation => new ToolCallOutcome(
                new AiToolCallResult(call.Name, call.Id, true, null, null, null, new
                {
                    status = "awaiting_confirmation",
                    item_count = executionResponse.PendingOperation?.ChangeTargetCount,
                    habit_count = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?.Select(item => item.EntityId).Distinct().Count() : (int?)null,
                    date_count = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?.SelectMany(item => item.Fields).Where(field => field.Field == "date").Select(field => field.NewValue).Distinct().Count() : (int?)null,
                    proposed_titles = executionResponse.PendingOperation?.Items?.Select(item => item.EntityName).Distinct().Take(10).ToArray(),
                    proposed_values = call.Args.EnumerateObject().Where(property =>
                        property.Value.ValueKind is JsonValueKind.Number || property.Name is "title" or "deadline" or "unit")
                        .ToDictionary(property => property.Name, property => property.Value.Clone()),
                    resolved_habits = executionResults.Habits.Where(habit => call.Args.GetRawText().Contains(habit.Id.ToString(), StringComparison.OrdinalIgnoreCase)).Select(habit => new { habit.Id, habit.Title }).ToArray(),
                    dates = call.Name == "bulk_log_habits" ? executionResponse.PendingOperation?.Items?
                        .SelectMany(item => item.Fields).Where(field => field.Field == "date")
                        .Select(field => field.NewValue).Distinct().Order().ToArray() : null,
                    instruction = "This is a proposal. Nothing has changed until approval. Use only the returned numbers, names, and dates."
                }),
                null,
                null,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation),
            AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy => new ToolCallOutcome(
                toolResult,
                tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: toolResult.Error),
                operationResult,
                executionResponse.PolicyDenial,
                null),
            _ => new ToolCallOutcome(
                toolResult,
                BuildActionResult(call, tool, ToToolResult(operationResult)),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation)
        };
    }

    private static ToolCallOutcome UnknownToolOutcome(AiToolCall call)
    {
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, $"Unknown tool: {call.Name}"),
            new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: $"Unknown tool: {call.Name}"),
            new AgentOperationResult(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                PolicyReason: UnsupportedByPolicyReason),
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
            tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: "Operation is unsupported by policy."),
            new AgentOperationResult(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                Summary: BuildOperationSummary(call),
                PolicyReason: UnsupportedByPolicyReason),
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
            OnExecutionStarted: onExecutionStarted), cancellationToken);
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
            LogToolFailed(logger, call.Name, operationResult.PolicyReason);
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
                    Error: "Tool arguments exceeded the clarification stash limit."),
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
                ClarificationRequest: clarification),
            operationResult,
            executionResponse.PolicyDenial,
            executionResponse.PendingOperation);
    }

    private sealed record ToolCallOutcome(
        AiToolCallResult ToolResult,
        ActionResult? ActionResult,
        AgentOperationResult? OperationResult,
        AgentPolicyDenial? PolicyDenial,
        PendingAgentOperation? PendingOperation);
}
