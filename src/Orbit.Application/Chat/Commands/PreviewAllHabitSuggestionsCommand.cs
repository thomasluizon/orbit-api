using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public sealed record PreviewAllHabitSuggestionsRequest(IReadOnlyList<HabitSuggestionDraft> Drafts, string Language = "en");

public sealed record HabitSuggestionPreviewErrorResponse(string Error, string? ErrorCode, int DraftIndex);

public sealed class HabitSuggestionPreviewFailure(Result failure, int draftIndex)
    : Result<AgentExecuteOperationResponse>(null, false, failure.Error, failure.ErrorCode, failure.ErrorArgs)
{
    public int DraftIndex { get; } = draftIndex;
}

public sealed record PreviewAllHabitSuggestionsCommand(Guid UserId, IReadOnlyList<HabitSuggestionDraft> Drafts,
    string Language = "en", AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt, IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false) : IRequest<Result<AgentExecuteOperationResponse>>;

public sealed class PreviewAllHabitSuggestionsCommandHandler(HabitSuggestionService suggestions, IAgentOperationExecutor executor,
    AccountChangeSetService changeSets, IPendingAgentOperationStore store)
    : IRequestHandler<PreviewAllHabitSuggestionsCommand, Result<AgentExecuteOperationResponse>>
{
    public async Task<Result<AgentExecuteOperationResponse>> Handle(PreviewAllHabitSuggestionsCommand request, CancellationToken ct)
    {
        if (request.Drafts is null || !HabitSuggestionsCard.IsValidDraftCount(request.Drafts.Count))
            return Result.Failure<AgentExecuteOperationResponse>("Invalid suggestion count.", ValidationErrorCodes.HabitSuggestionCount);
        var drafts = new List<HabitSuggestionDraft>();
        for (var index = 0; index < request.Drafts.Count; index++)
        {
            var checkedDraft = await suggestions.CheckForPreviewAsync(request.UserId, request.Drafts[index], request.Language, ct);
            if (checkedDraft.IsFailure) return new HabitSuggestionPreviewFailure(checkedDraft, index);
            drafts.Add(checkedDraft.Value);
        }
        var pending = new List<PendingAgentOperation>();
        var complete = false;
        try
        {
            foreach (var draft in drafts)
            {
                var arguments = PreviewHabitSuggestionCommandHandler.Arguments(draft);
                var operationId = draft.ExistingHabitId.HasValue ? "update_habit" : "create_habit";
                if (!draft.ExistingHabitId.HasValue && drafts.Count > 1)
                {
                    operationId = "bulk_create_habits";
                    arguments = JsonSerializer.SerializeToElement(new { habits = new[] { arguments } });
                }
                var response = await executor.ExecuteAsync(new(request.UserId,
                    operationId, arguments,
                    AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
                    IncludeChangePreview: true, PreviewOnly: true), ct);
                if (response.PendingOperation is { } member) pending.Add(member);
                if (response.Operation.Status != AgentOperationStatus.PendingConfirmation || response.PendingOperation is null)
                    return Result.Success(response);
                if (drafts.Count == 1)
                {
                    complete = true;
                    return Result.Success(response);
                }
            }
            var combined = await changeSets.CombineAsync(request.UserId, pending, ct, requireAllMembers: true);
            if (combined is null)
                return Result.Failure<AgentExecuteOperationResponse>(HabitSuggestionService.Unavailable(request.Language), "HABIT_SUGGESTION_UNAVAILABLE");
            complete = true;
            var operation = combined.PendingOperation;
            return Result.Success(new AgentExecuteOperationResponse(new(AccountChangeSet.OperationName, AccountChangeSet.OperationName,
                operation.RiskClass, operation.ConfirmationRequirement, AgentOperationStatus.PendingConfirmation,
                Summary: operation.Summary, PendingOperationId: operation.Id), operation));
        }
        finally
        {
            if (!complete)
                foreach (var operation in pending)
                    if (store.GetExecution(request.UserId, operation.Id) is { } execution)
                        store.Cancel(request.UserId, operation.Id,
                            AgentOperationFingerprint.Compute(execution.OperationId, execution.Arguments.GetRawText()));
        }
    }
}
