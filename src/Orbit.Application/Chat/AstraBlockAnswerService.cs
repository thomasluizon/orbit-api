using System.Text.Json;
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

public sealed record PreparedAstraBlockAnswer(
    string Message, ChatResponse? Response = null, CrisisLocales Crisis = CrisisLocales.None,
    DateTime? AnsweredAtUtc = null, DateOnly? ReservedAllowanceDate = null, bool IsReplay = false);

public sealed class AstraBlockAnswerService(
    IIssuedAstraBlockStore store,
    IUnitOfWork unitOfWork,
    IPayGateService payGate,
    IAgentOperationExecutor executor,
    IGenericRepository<User> users,
    IProductAnalytics analytics,
    IFeatureFlagService featureFlags,
    IPendingAgentOperationStore pendingOperations,
    ILogger? logger = null)
{
    public async Task<Result<ChatResponse>> ExecuteAsync(ProcessUserChatCommand request,
        Func<PreparedAstraBlockAnswer, Task<Result<ChatResponse>>> produceResponse, CancellationToken ct)
    {
        var preparation = await PrepareAsync(request, ct);
        if (preparation.IsFailure)
            return preparation.PropagateError<ChatResponse>();
        var prepared = preparation.Value;
        if (prepared.Response is { } storedResponse)
        {
            if (!prepared.IsReplay)
                await CaptureAnsweredAsync(request, CancellationToken.None);
            return Result.Success(storedResponse);
        }

        var completed = false;
        try
        {
            var result = await produceResponse(prepared);
            if (result.IsFailure)
                return result;
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                if (!await store.CompleteAnswerAsync(request.BlockAnswer!.BlockId, request.UserId,
                        prepared.AnsweredAtUtc!.Value, JsonSerializer.Serialize(result.Value), token))
                    throw new InvalidOperationException("Block answer reservation was lost.");
            }, CancellationToken.None);
            completed = true;
            await CaptureAnsweredAsync(request, CancellationToken.None);
            return result;
        }
        finally
        {
            if (!completed)
                await ReleaseAsync(request, prepared);
        }
    }

    private async Task ReleaseAsync(ProcessUserChatCommand request, PreparedAstraBlockAnswer prepared)
    {
        unitOfWork.ResetTracking();
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
            if (!await store.ReleaseAnswerAsync(request.BlockAnswer!.BlockId, request.UserId,
                    prepared.AnsweredAtUtc!.Value, token))
                return;
            if (prepared.ReservedAllowanceDate is { } reservedDate)
            {
                var refund = await payGate.RefundAiMessage(request.UserId, reservedDate, unitOfWork, token);
                if (refund.IsFailure)
                    throw new InvalidOperationException(refund.Error);
            }
        }, CancellationToken.None);
    }

    public async Task<Result<PreparedAstraBlockAnswer>> PrepareAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        var answer = request.BlockAnswer ?? throw new ArgumentException("Block answer is required.", nameof(request));
        var validation = await new AstraBlockAnswerValidator().ValidateAsync(answer, ct);
        if (!validation.IsValid)
            return Result.Failure<PreparedAstraBlockAnswer>(validation.Errors[0].ErrorMessage, validation.Errors[0].ErrorCode);
        if (request.AuthMethod != AgentAuthMethod.Jwt)
            return Failure(ErrorCodes.AstraBlockNotFound);
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                var block = await store.GetAsync(answer.BlockId, request.UserId, token);
                if (ReadReplay(block, answer, request.UserId) is { } replay)
                    return Result.Success(new PreparedAstraBlockAnswer(string.Empty, replay, IsReplay: true));
                var stateError = CheckState(block);
                if (stateError is not null)
                    return stateError;
                var definition = block!.ReadDefinition();
                var answerError = CheckAnswer(definition, answer);
                if (answerError is not null)
                    return answerError;
                var isChoice = definition.Form == AstraBlockKinds.Choice;
                var message = BuildMessage(definition, answer);
                var crisis = isChoice ? await DetectCrisisTurnAsync(request.UserId, message, token) : CrisisLocales.None;
                if (isChoice && crisis == CrisisLocales.None)
                {
                    var allowance = await payGate.CanSendAiMessage(request.UserId, token);
                    if (allowance.IsFailure)
                        return allowance.PropagateError<PreparedAstraBlockAnswer>();
                }
                var answeredAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                if (!await store.MarkAnsweredAsync(block.Id, request.UserId, answeredAtUtc, token))
                    return CheckState(await store.GetAsync(block.Id, request.UserId, token))
                        ?? Failure(ErrorCodes.AstraBlockAnswered);

                var user = await users.GetByIdAsync(request.UserId, token);
                ChatResponse? response = null;
                DateOnly? reservedAllowanceDate = null;
                if (definition.Form == AstraBlockKinds.PickList)
                {
                    response = await BuildPickListResponseAsync(request, definition, answer, user, token);
                }
                else if (crisis == CrisisLocales.None)
                {
                    var reservation = await payGate.TryConsumeAiMessage(request.UserId, unitOfWork, token);
                    if (reservation.IsFailure)
                        throw new AnswerRejectedException(reservation.PropagateError<PreparedAstraBlockAnswer>());
                    reservedAllowanceDate = (await users.GetByIdAsync(request.UserId, token))?.AiMessagesLocalDate;
                }
                await store.RecordAnswerAsync(block.Id, request.UserId, answeredAtUtc,
                    IssuedAstraBlock.SerializeAnswer(answer), reservedAllowanceDate,
                    response is null ? null : JsonSerializer.Serialize(response), token);
                return Result.Success(new PreparedAstraBlockAnswer(message, response, crisis,
                    answeredAtUtc, reservedAllowanceDate));
            }, ct);
        }
        catch (AnswerRejectedException exception)
        {
            return exception.Result;
        }
    }

    private async Task<ChatResponse> BuildPickListResponseAsync(ProcessUserChatCommand request,
        AstraBlockDefinition definition, AstraBlockAnswer answer, User? user, CancellationToken token)
    {
        var chosen = definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).ToArray();
        var preview = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            request.UserId, "bulk_" + definition.Action,
            AskTool.BuildBulkArguments(definition.Action!, chosen), AgentExecutionSurface.Chat,
            request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
            CorrelationId: request.CorrelationId, IncludeChangePreview: true, PreviewOnly: true), token);
        if (preview.Operation.Status != AgentOperationStatus.PendingConfirmation
            || preview.PendingOperation?.Items?.Count != chosen.Length)
            throw new AnswerRejectedException(Failure(ErrorCodes.AstraBlockPreviewUnavailable));
        var line = LocaleHelper.IsPortuguese(user?.Language) ? "Nada foi salvo." : "Nothing has been saved.";
        return new ChatResponse(line, [], PendingOperations: [preview.PendingOperation],
            CorrelationId: request.CorrelationId,
            Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(line)] : null);
    }

    private ChatResponse? ReadReplay(IssuedAstraBlock? block, AstraBlockAnswer answer, Guid userId)
    {
        if (block is not { AnsweredAtUtc: not null, ResponseJson: not null } || !block.MatchesAnswer(answer))
            return null;
        var stored = JsonSerializer.Deserialize<ChatResponse>(block.ResponseJson)
            ?? throw new InvalidOperationException("Stored block has no response.");
        return stored.PendingOperations is { Count: > 0 } pending
            ? stored with
            {
                PendingOperations = pending.Where(operation =>
                pendingOperations.IsAwaitingConfirmation(userId, operation.Id)).ToArray()
            }
            : stored;
    }

    private static string BuildMessage(AstraBlockDefinition definition, AstraBlockAnswer answer)
    {
        var labels = definition.Form == AstraBlockKinds.Choice
            ? definition.Options.Where(option => answer.SelectedIds.Contains(option.Id)).Select(option => option.Label)
            : definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).Select(item => item.Label);
        var message = string.Join(", ", labels.Concat(string.IsNullOrWhiteSpace(answer.FreeText) ? [] : new[] { answer.FreeText }));
        return definition.Form == AstraBlockKinds.Choice ? definition.Title + "\n" + message : message;
    }

    private async Task<CrisisLocales> DetectCrisisTurnAsync(Guid userId, string message, CancellationToken ct)
    {
        var locales = CrisisSupportGuard.Detect(message);
        if (locales == CrisisLocales.None)
            return locales;
        var enabledFlags = await featureFlags.GetEnabledKeysForUserAsync(userId, ct);
        return CrisisSupportGuard.IsEnabled(locales, enabledFlags) ? locales : CrisisLocales.None;
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
            return Failure(ErrorCodes.AstraBlockNotFound);
        if (block.AnsweredAtUtc.HasValue)
            return Failure(ErrorCodes.AstraBlockAnswered);
        if (block.InvalidatedAtUtc.HasValue)
            return Failure(ErrorCodes.AstraBlockInvalidated);
        if (block.ExpiresAtUtc <= TimeProvider.System.GetUtcNow().UtcDateTime)
            return Failure(ErrorCodes.AstraBlockExpired);
        return null;
    }

    private static Result<PreparedAstraBlockAnswer>? CheckAnswer(AstraBlockDefinition block, AstraBlockAnswer answer)
    {
        var ids = block.Form == AstraBlockKinds.Choice ? block.Options.Select(option => option.Id) : block.Items.Select(item => item.Id);
        if (answer.SelectedIds.Except(ids, StringComparer.Ordinal).Any())
            return Failure(ErrorCodes.AstraBlockUnknownSelection);
        if (!block.MultiSelect && answer.SelectedIds.Count > 1)
            return Failure(ErrorCodes.AstraBlockSingleSelection);
        if (block.MultiSelect && answer.SelectedIds.Count == 0)
            return Failure(ErrorCodes.AstraBlockEmptySelection);
        if (answer.FreeText is not null && !block.AllowFreeText)
            return Failure(ErrorCodes.AstraBlockFreeTextDisabled);
        if (answer.SelectedIds.Count == 0 && string.IsNullOrWhiteSpace(answer.FreeText))
            return Failure(ErrorCodes.AstraBlockEmptyAnswer);
        return null;
    }

    private static Result<PreparedAstraBlockAnswer> Failure(string code) =>
        Result.Failure<PreparedAstraBlockAnswer>(ErrorCopy.English(code), code);

    private sealed class AnswerRejectedException(Result<PreparedAstraBlockAnswer> result) : Exception
    {
        public Result<PreparedAstraBlockAnswer> Result { get; } = result;
    }
}
