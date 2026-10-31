using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool RequiresTerminalAnswer(AiResponse response, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results) =>
        !response.HasToolCalls && response.ConversationContext is not null
        && UsesComposition(request, context) && AskEnabled(request, context)
        && results.AskBlock is null && results.PendingSelection is null
        && results.PendingOperations.Count == 0 && results.ActionResults.Count == 0
        && !results.IsPreviewDiscussion && !CanCompleteComposition(request, context, results)
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded);

    private async Task<ToolRoundResult> CorrectTerminalAnswerAsync(AiResponse response, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results, bool attempted, int iteration, long budgetTokens,
        CancellationToken ct)
    {
        if (attempted || !HasAnswerCorrectionAllowance(request, context, iteration, budgetTokens))
            return InvalidTerminalAnswer(response);
        response.ConversationContext!.ResponseValidationFeedback = TerminalAnswerDirective.ValidationFeedback;
        RefreshAskSchema(response.ConversationContext, request, context, results);
        var corrected = await ai.IntentService.ContinueWithToolResultsAsync(response.ConversationContext, [], null, ct);
        return corrected.IsSuccess ? new(corrected.Value)
            : new(response, ProviderError: new AppError(corrected.ErrorCode ?? ErrorMessages.AiUnavailable.Code, corrected.Error));
    }

    private static ToolRoundResult InvalidTerminalAnswer(AiResponse response) =>
        new(response, ProviderError: ErrorMessages.AiUnavailable);

    private static bool HasAnswerCorrectionAllowance(ProcessUserChatCommand request, ChatContext context,
        int iteration, long budgetTokens) => iteration < ChatTurnLimits.MaxToolIterations
        && (context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraEvalUnmetered, StringComparer.Ordinal)
            || budgetTokens < AppConstants.MaxAiRequestTotalTokens)
        && request.BlockAnswer is null && request.ConfirmationToken is null;

    private static ToolLoopResult ValidateTerminalAnswer(ToolLoopResult loop, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        var response = loop.FinalResponse;
        if (!RequiresTerminalAnswer(response, request, context, results)) return loop;
        if (TerminalAnswerDirective.TryRead(response.TextMessage, out var answer))
            return loop with { FinalResponse = response with { TextMessage = answer, ConversationContext = null } };
        return response.IsTruncated
            ? loop with { FinalResponse = response with { TextMessage = null, ConversationContext = null } }
            : loop with { ProviderError = ErrorMessages.AiUnavailable };
    }
}
