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
        AiToolRequest originalAiRequest,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        var aiResponse = initialResponse;
        var iteration = 0;
        long totalReportedTokens = initialResponse.ReportedTokenCount;
        var tokenBudgetExceeded = totalReportedTokens > AppConstants.MaxAiRequestTotalTokens;
        var rejectedCalls = new List<RejectedToolCall>();
        var isRetryRound = false;
        var hadToolFailure = false;
        var retrySucceeded = false;

        if (tokenBudgetExceeded)
        {
            LogAiRequestTokenCeilingReached(
                logger,
                totalReportedTokens,
                AppConstants.MaxAiRequestTotalTokens,
                iteration);
        }

        while (aiResponse.HasToolCalls && iteration < MaxToolIterations && !tokenBudgetExceeded)
        {
            iteration++;
            if (request.StreamSink is not null)
                await request.StreamSink(ChatStreamEvent.Round(iteration));

            var roundResult = await ProcessToolCallsAsync(
                aiResponse,
                originalAiRequest,
                request,
                executionResults,
                rejectedCalls,
                isRetryRound,
                iteration,
                aiStreamSink,
                cancellationToken);

            if (roundResult is null)
                break;

            aiResponse = roundResult.Response;
            if (roundResult.StartedRecovery)
            {
                hadToolFailure = true;
                isRetryRound = true;
            }
            else if (isRetryRound)
            {
                retrySucceeded = roundResult.RetrySucceeded;
                if (aiResponse.HasToolCalls)
                {
                    aiResponse = MessageResponse(retrySucceeded
                        ? ToolFailureRecoveredMessageKey
                        : ToolFailureMessageKey);
                }
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
            hadToolFailure,
            retrySucceeded);
    }

    private sealed record ToolLoopResult(
        AiResponse FinalResponse,
        int Iterations,
        bool TokenBudgetExceeded,
        bool HadToolFailure,
        bool RetrySucceeded);

    /// <summary>
    /// Processes one iteration of AI tool calls: orders them, executes each, and sends results
    /// back to the AI. Returns the next AI response, or null if continuation failed.
    /// </summary>
    private async Task<ToolRoundResult?> ProcessToolCallsAsync(
        AiResponse aiResponse,
        AiToolRequest originalAiRequest,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        List<RejectedToolCall> rejectedCalls,
        bool isRetryRound,
        int iteration,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        LogToolCallingIteration(logger, iteration, aiResponse.ToolCalls!.Count);

        var orderedCalls = aiResponse.ToolCalls!
            .OrderBy(c => ai.ToolRegistry.GetTool(c.Name)?.Order ?? int.MaxValue)
            .ToList();

        if (isRetryRound && orderedCalls.Any(call => IsIdenticalRejectedCall(call, rejectedCalls)))
            return new ToolRoundResult(MessageResponse(ToolFailureMessageKey), RetrySucceeded: false);

        var outcomesByCallId = await ExecuteToolCallsAsync(orderedCalls, request, cancellationToken);

        var toolResults = new List<AiToolCallResult>(orderedCalls.Count);
        var retryableFailures = new List<RejectedToolCall>();
        foreach (var call in orderedCalls)
        {
            var outcome = outcomesByCallId[call.Id];
            toolResults.Add(outcome.ToolResult);
            executionResults.Add(
                call.Name,
                outcome.ActionResult,
                outcome.OperationResult,
                outcome.PolicyDenial,
                outcome.PendingOperation,
                isRetryRound);

            if (outcome.OperationResult?.Status == AgentOperationStatus.Failed)
                retryableFailures.Add(new RejectedToolCall(call.Name, call.Args.Clone(), outcome.ToolResult.Error));
        }

        if (isRetryRound)
        {
            if (retryableFailures.Count > 0)
                return new ToolRoundResult(MessageResponse(ToolFailureMessageKey), RetrySucceeded: false);

            var retryContinuation = await ai.IntentService.ContinueWithToolResultsAsync(
                aiResponse.ConversationContext!, toolResults, streamSink: null, cancellationToken);
            if (retryContinuation.IsFailure || retryContinuation.Value.HasToolCalls)
                return new ToolRoundResult(MessageResponse(ToolFailureRecoveredMessageKey), RetrySucceeded: true);

            return new ToolRoundResult(retryContinuation.Value, RetrySucceeded: true);
        }

        if (retryableFailures.Count > 0)
        {
            rejectedCalls.AddRange(retryableFailures);
            var retryRequest = originalAiRequest with
            {
                PriorToolFailures = retryableFailures
                    .Select(failure => new AiToolFailure(failure.Name, failure.Error))
                    .ToList()
            };
            var retryResponse = await ai.IntentService.SendWithToolsAsync(
                retryRequest, streamSink: null, cancellationToken);
            if (retryResponse.IsFailure || !retryResponse.Value.HasToolCalls)
            {
                return new ToolRoundResult(
                    MessageResponse(ToolFailureMessageKey),
                    StartedRecovery: true,
                    RetrySucceeded: false);
            }

            return new ToolRoundResult(retryResponse.Value, StartedRecovery: true);
        }

        var continueResult = await ai.IntentService.ContinueWithToolResultsAsync(aiResponse.ConversationContext!, toolResults, aiStreamSink, cancellationToken);
        if (continueResult.IsFailure)
        {
            LogContinueWithToolResultsFailed(logger, continueResult.Error);
            return null;
        }

        return new ToolRoundResult(continueResult.Value);
    }

    private sealed record ToolRoundResult(
        AiResponse Response,
        bool StartedRecovery = false,
        bool RetrySucceeded = false);

    private sealed record RejectedToolCall(string Name, JsonElement Arguments, string? Error);

    private static AiResponse MessageResponse(string messageKey) => new() { TextMessage = messageKey };

    private static bool IsIdenticalRejectedCall(AiToolCall call, IReadOnlyList<RejectedToolCall> rejectedCalls)
    {
        return rejectedCalls.Any(rejected =>
            string.Equals(rejected.Name, call.Name, StringComparison.Ordinal)
            && JsonValuesEqual(rejected.Arguments, call.Args));
    }

    private static bool JsonValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectsEqual(left, right),
            JsonValueKind.Array => JsonArraysEqual(left, right),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static bool JsonObjectsEqual(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToList();
        var rightProperties = right.EnumerateObject().ToList();
        if (leftProperties.Count != rightProperties.Count)
            return false;

        return leftProperties.All(leftProperty =>
            right.TryGetProperty(leftProperty.Name, out var rightValue)
            && JsonValuesEqual(leftProperty.Value, rightValue));
    }

    private static bool JsonArraysEqual(JsonElement left, JsonElement right)
    {
        var leftItems = left.EnumerateArray().ToList();
        var rightItems = right.EnumerateArray().ToList();
        return leftItems.Count == rightItems.Count
            && leftItems.Zip(rightItems).All(pair => JsonValuesEqual(pair.First, pair.Second));
    }

    private static bool ContainsInternalToolVocabulary(string? message, IReadOnlyList<object> toolDeclarations)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (message.Contains("frequência diária", StringComparison.OrdinalIgnoreCase))
            return true;

        var argumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in toolDeclarations)
            CollectToolArgumentNames(JsonSerializer.SerializeToElement(declaration), argumentNames);

        return argumentNames.Any(name => ContainsIdentifier(message, name));
    }

    private static string? SanitizeToolFailureMessage(
        string? message,
        ToolLoopResult toolLoopResult,
        IReadOnlyList<object> toolDeclarations)
    {
        if (!toolLoopResult.HadToolFailure
            || !ContainsInternalToolVocabulary(message, toolDeclarations))
        {
            return message;
        }

        return toolLoopResult.RetrySucceeded
            ? ToolFailureRecoveredMessageKey
            : ToolFailureMessageKey;
    }

    private static void CollectToolArgumentNames(JsonElement element, HashSet<string> argumentNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var argument in property.Value.EnumerateObject())
                        argumentNames.Add(argument.Name);
                }

                CollectToolArgumentNames(property.Value, argumentNames);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectToolArgumentNames(item, argumentNames);
        }
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var index = text.IndexOf(identifier, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var end = index + identifier.Length;
            var hasLeftBoundary = index == 0 || !IsIdentifierCharacter(text[index - 1]);
            var hasRightBoundary = end == text.Length || !IsIdentifierCharacter(text[end]);
            if (hasLeftBoundary && hasRightBoundary)
                return true;

            searchStart = index + 1;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>
    /// Executes a round's tool calls, dispatching the read-only subset concurrently (each on its
    /// own DI scope for DbContext isolation) and the write subset sequentially on the ambient
    /// scope in <c>Order</c>. Returns every outcome keyed by tool-call id so the caller can
    /// reassemble results deterministically, independent of task-completion timing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ToolCallOutcome>> ExecuteToolCallsAsync(
        List<AiToolCall> orderedCalls,
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var readOnlyCalls = orderedCalls
            .Where(call => ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true)
            .ToList();
        var writeCalls = orderedCalls
            .Where(call => ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly != true)
            .ToList();

        var readOnlyTasks = readOnlyCalls
            .Select(call => ExecuteReadOnlyToolCallOnIsolatedScopeAsync(call, request, cancellationToken))
            .ToList();
        var readOnlyOutcomes = await Task.WhenAll(readOnlyTasks);

        var outcomesByCallId = new Dictionary<string, ToolCallOutcome>(orderedCalls.Count, StringComparer.Ordinal);
        for (var index = 0; index < readOnlyCalls.Count; index++)
            outcomesByCallId[readOnlyCalls[index].Id] = readOnlyOutcomes[index];

        foreach (var call in writeCalls)
        {
            outcomesByCallId[call.Id] = await ExecuteSingleToolCallAsync(
                call, request, execution.OperationExecutor, execution.PendingClarificationStore, cancellationToken);
        }

        return outcomesByCallId;
    }

    private async Task<ToolCallOutcome> ExecuteReadOnlyToolCallOnIsolatedScopeAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        using var scope = execution.ServiceScopeFactory.CreateScope();
        var scopedExecutor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();
        var scopedClarificationStore = scope.ServiceProvider.GetRequiredService<IPendingClarificationStore>();

        return await ExecuteSingleToolCallAsync(
            call, request, scopedExecutor, scopedClarificationStore, cancellationToken);
    }

    /// <summary>
    /// Executes a single tool call: resolves the tool, runs it, and produces both a result
    /// for the AI and an optional action result for the frontend.
    /// </summary>
    private async Task<ToolCallOutcome> ExecuteSingleToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
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

        var executionResponse = await DispatchToolCallAsync(call, request, operationExecutor, cancellationToken);
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
                new AiToolCallResult(call.Name, call.Id, false, null, null, "Confirmation required before this action can run."),
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
            request.CorrelationId), cancellationToken);
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
