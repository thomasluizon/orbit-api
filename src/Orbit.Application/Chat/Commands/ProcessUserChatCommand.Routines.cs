using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<ChatResponse>?> RecoverRoutinePreviewAsync(
        ProcessUserChatCommand request, ChatContext context, Result<AiResponse> response,
        ToolExecutionAccumulator? results, CancellationToken ct)
    {
        if (request.BlockAnswer is not null || request.ConfirmationToken is not null || request.ImageData is not null
            || results is not null && (HasSucceededWrite(results) || results.PendingOperations.Count > 0)
            || response.IsFailure && response.ErrorCode != ErrorCodes.AiEmptyResponse
            || response.IsSuccess && !IsRoutineClarification(response.Value)
            || ai.ToolRegistry.GetTool("create_habit") is null
            || RoutineCreationProposal.Read(request.Message) is not { } arguments)
            return null;
        var executionResults = new ToolExecutionAccumulator
        {
            Habits = context.ActiveHabits,
            AllHabits = context.UserHabits
        };
        SeedProposedHabitNames(request, executionResults);
        var call = new AiToolCall("create_habit", Guid.NewGuid().ToString(), arguments);
        var outcomes = await ExecuteToolCallsAsync([call], request, executionResults, GetUserLanguage(context.User), ct);
        var outcome = outcomes[call.Id];
        return outcome.PendingOperation is { } pending
            ? Result.Success(PendingParentReply(request, pending))
            : null;
    }

    private static bool IsRoutineClarification(AiResponse response) =>
        !response.IsTruncated && response.ReportedTokenCount <= AppConstants.MaxAiRequestTotalTokens
        && (response.ToolCalls is null || response.ToolCalls.All(call => call.Name == "ask")
            || response.ToolCalls is { Count: 1 } calls && calls[0] is { Name: "create_habit" } call
                && call.Args.ValueKind == JsonValueKind.Object
                && (!call.Args.TryGetProperty("frequency_unit", out var frequency) || frequency.ValueKind == JsonValueKind.Null));
}
