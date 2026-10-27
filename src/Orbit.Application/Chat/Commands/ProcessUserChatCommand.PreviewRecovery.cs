using System.Text.Json;
using Orbit.Domain.Models;
using Orbit.Application.Common;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ToolRoundResult?> TryRecoverFreshCreateAsync(AiResponse response,
        IReadOnlyList<AiToolCall> calls, IReadOnlyDictionary<string, ToolCallOutcome> outcomes,
        List<AiToolCallResult> toolResults, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, int iteration, long totalBudgetTokens, string? language, CancellationToken ct)
    {
        if (calls is not [{ Name: "revise_pending_changes" } revision]
            || outcomes[revision.Id].OperationResult is not { Status: AgentOperationStatus.Failed, PolicyReason: "invalid_revision" } failure
            || !CanRecoverFreshCreate(request, context, results, iteration, totalBudgetTokens)
            || !revision.Args.TryGetProperty("pending_operation_id", out var pendingId)
            || pendingId.ValueKind != JsonValueKind.String || !Guid.TryParse(pendingId.GetString(), out var id)
            || execution.PendingOperationStore.GetExecution(request.UserId, id)?.OperationId is not ("create_habit" or "bulk_create_habits" or "create_sub_habit"))
            return null;

        var recoveryResult = toolResults.Single() with
        {
            Payload = new
            {
                instruction = "This turn has not produced a preview. The attempted revision failed and changed nothing. If the person asked to create a habit, call create_habit, bulk_create_habits or create_sub_habit with the requested details to prepare a new approval preview. Keep the older preview unchanged. Otherwise emit no tool calls. Do not claim anything was saved."
            }
        };
        ct.ThrowIfCancellationRequested();
        var replanned = await ai.IntentService.ContinueWithToolResultsAsync(response.ConversationContext!,
            [recoveryResult], null, ct);
        ct.ThrowIfCancellationRequested();
        if (replanned.IsFailure || replanned.Value.IsTruncated
            || !HasPreviewRecoveryBudget(context, totalBudgetTokens
                + (replanned.Value.RequestBudgetTokenCount ?? replanned.Value.ReportedTokenCount))
            || replanned.Value.ToolCalls is not [{ Name: "create_habit" or "bulk_create_habits" or "create_sub_habit" } creation])
            return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);

        creation = NormalizeExecutionArguments(creation);
        var tool = ai.ToolRegistry.GetTool(creation.Name);
        if (tool is not IArgumentCheckTool check
            || !ChatToolArgumentCheck.HasValidShape(tool, creation.Args)
            || (await check.CheckArgumentsAsync(creation.Args, request.UserId, ct)).IsFailure)
            return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);
        var recoveredOutcomes = await ExecuteToolCallsAsync([creation], request, results, language, ct);
        RecordToolRound(replanned.Value, [creation], recoveredOutcomes, results, isCorrection: true);
        if (recoveredOutcomes[creation.Id].PendingOperation is null)
            return new ToolRoundResult(MessageResponse(language), HadToolFailure: true);

        results.OperationResults.Remove(failure);
        results.ToolResults.Remove(outcomes[revision.Id].ToolResult);
        return new ToolRoundResult(new AiResponse
        {
            ReportedTokenCount = replanned.Value.ReportedTokenCount,
            RequestBudgetTokenCount = replanned.Value.RequestBudgetTokenCount
        });
    }

    private static bool CanRecoverFreshCreate(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, int iteration, long tokens) =>
        IsExplicitHabitCreate(request.Message)
        && request.BlockAnswer is null && request.ConfirmationToken is null
        && results.PendingOperations.Count == 0 && results.ActionResults.Count == 0
        && results.OperationResults.Count == 1 && iteration < ChatTurnLimits.MaxToolIterations
        && HasPreviewRecoveryBudget(context, tokens + 1);

    private static bool HasPreviewRecoveryBudget(ChatContext context, long tokens) =>
        context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraEvalUnmetered, StringComparer.Ordinal)
        || tokens <= AppConstants.MaxAiRequestTotalTokens;
}
