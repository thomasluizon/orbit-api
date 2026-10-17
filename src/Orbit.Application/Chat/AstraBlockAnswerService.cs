using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record PreparedAstraBlockAnswer(string Message, ChatResponse? Response = null);

public sealed class AstraBlockAnswerService(
    IIssuedAstraBlockStore store,
    IUnitOfWork unitOfWork,
    IPayGateService payGate,
    IAgentOperationExecutor executor,
    IGenericRepository<User> users,
    IProductAnalytics analytics,
    ILogger? logger = null)
{
    public async Task<Result<PreparedAstraBlockAnswer>> PrepareAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        var answer = request.BlockAnswer ?? throw new ArgumentException("Block answer is required.", nameof(request));
        var validation = await new AstraBlockAnswerValidator().ValidateAsync(answer, ct);
        if (!validation.IsValid)
            return Result.Failure<PreparedAstraBlockAnswer>(validation.Errors[0].ErrorMessage, validation.Errors[0].ErrorCode);
        if (request.AuthMethod != AgentAuthMethod.Jwt)
            return Failure(ErrorCodes.AstraBlockNotFound, "Block not found.");
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                var block = await store.GetAsync(answer.BlockId, request.UserId, token);
                var stateError = CheckState(block);
                if (stateError is not null)
                    return stateError;
                var definition = block!.ReadDefinition();
                var answerError = CheckAnswer(definition, answer);
                if (answerError is not null)
                    return answerError;
                if (definition.Form == AstraBlockKinds.Choice)
                {
                    var allowance = await payGate.CanSendAiMessage(request.UserId, token);
                    if (allowance.IsFailure)
                        return allowance.PropagateError<PreparedAstraBlockAnswer>();
                }
                var answeredAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                if (!await store.MarkAnsweredAsync(block.Id, request.UserId, answeredAtUtc, token))
                    return CheckState(await store.GetAsync(block.Id, request.UserId, token))
                        ?? Failure(ErrorCodes.AstraBlockAnswered, "Block already answered.");

                var user = await users.GetByIdAsync(request.UserId, token);
                var labels = definition.Form == AstraBlockKinds.Choice
                    ? definition.Options.Where(option => answer.SelectedIds.Contains(option.Id)).Select(option => option.Label)
                    : definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).Select(item => item.Label);
                var message = string.Join(", ", labels.Concat(string.IsNullOrWhiteSpace(answer.FreeText) ? [] : new[] { answer.FreeText }));
                ChatResponse? response = null;
                if (definition.Form == AstraBlockKinds.PickList)
                {
                    var chosen = definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).ToArray();
                    var preview = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
                        request.UserId, "bulk_" + definition.Action,
                        AskTool.BuildBulkArguments(definition.Action!, chosen), AgentExecutionSurface.Chat,
                        request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
                        CorrelationId: request.CorrelationId, IncludeChangePreview: true, PreviewOnly: true), token);
                    if (preview.Operation.Status != AgentOperationStatus.PendingConfirmation
                        || preview.PendingOperation?.Items?.Count != chosen.Length)
                        throw new AnswerRejectedException(Result.Failure<PreparedAstraBlockAnswer>(
                            "The selected items could not be previewed.", "ASTRA_BLOCK_PREVIEW_UNAVAILABLE"));
                    var line = LocaleHelper.IsPortuguese(user?.Language) ? "Nada foi salvo." : "Nothing has been saved.";
                    response = new ChatResponse(line, [], PendingOperations: [preview.PendingOperation],
                        CorrelationId: request.CorrelationId,
                        Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(line)] : null);
                }
                else
                {
                    var reservation = await payGate.TryConsumeAiMessage(request.UserId, unitOfWork, token);
                    if (reservation.IsFailure)
                        throw new AnswerRejectedException(reservation.PropagateError<PreparedAstraBlockAnswer>());
                    message = definition.Title + "\n" + message;
                }
                return Result.Success(new PreparedAstraBlockAnswer(message, response));
            }, ct);
        }
        catch (AnswerRejectedException exception)
        {
            return exception.Result;
        }
    }

    public async Task CaptureAnsweredAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        var answer = request.BlockAnswer!;
        var block = await store.GetAsync(answer.BlockId, request.UserId, ct);
        if (block is null)
            return;
        var user = await users.GetByIdAsync(request.UserId, ct);
        AnalyticsCapture.SafeCaptureUserEvent(analytics, logger ?? NullLogger.Instance, request.UserId,
            user?.Plan.ToString() ?? "Free", "astra_block_answered",
            new Dictionary<string, object>
            {
                ["form"] = block.Form,
                ["selected_count"] = answer.SelectedIds.Count,
                ["used_free_text"] = !string.IsNullOrWhiteSpace(answer.FreeText),
                ["seconds_to_answer"] = Math.Max(0, ((block.AnsweredAtUtc ?? block.CreatedAtUtc) - block.CreatedAtUtc).TotalSeconds)
            });
    }

    private static Result<PreparedAstraBlockAnswer>? CheckState(IssuedAstraBlock? block)
    {
        if (block is null)
            return Failure(ErrorCodes.AstraBlockNotFound, "Block not found.");
        if (block.AnsweredAtUtc.HasValue)
            return Failure(ErrorCodes.AstraBlockAnswered, "Block already answered.");
        if (block.InvalidatedAtUtc.HasValue)
            return Failure(ErrorCodes.AstraBlockInvalidated, "Block was invalidated by a later message.");
        if (block.ExpiresAtUtc <= TimeProvider.System.GetUtcNow().UtcDateTime)
            return Failure(ErrorCodes.AstraBlockExpired, "Block expired.");
        return null;
    }

    private static Result<PreparedAstraBlockAnswer>? CheckAnswer(AstraBlockDefinition block, AstraBlockAnswer answer)
    {
        var ids = block.Form == AstraBlockKinds.Choice ? block.Options.Select(option => option.Id) : block.Items.Select(item => item.Id);
        if (answer.SelectedIds.Except(ids, StringComparer.Ordinal).Any())
            return Failure("ASTRA_BLOCK_UNKNOWN_SELECTION", "Selection is not in this block.");
        if (!block.MultiSelect && answer.SelectedIds.Count > 1)
            return Failure("ASTRA_BLOCK_SINGLE_SELECTION", "Choose one option.");
        if (block.MultiSelect && answer.SelectedIds.Count == 0)
            return Failure("ASTRA_BLOCK_EMPTY_SELECTION", "Choose at least one item.");
        if (answer.FreeText is not null && !block.AllowFreeText)
            return Failure("ASTRA_BLOCK_FREE_TEXT_DISABLED", "Free text is not allowed for this block.");
        if (answer.SelectedIds.Count == 0 && string.IsNullOrWhiteSpace(answer.FreeText))
            return Failure("ASTRA_BLOCK_EMPTY_ANSWER", "An answer is required.");
        return null;
    }

    private static Result<PreparedAstraBlockAnswer> Failure(string code, string message) =>
        Result.Failure<PreparedAstraBlockAnswer>(message, code);

    private sealed class AnswerRejectedException(Result<PreparedAstraBlockAnswer> result) : Exception
    {
        public Result<PreparedAstraBlockAnswer> Result { get; } = result;
    }
}
