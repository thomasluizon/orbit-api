using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
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
    DateTime? AnsweredAtUtc = null, DateOnly? ReservedAllowanceDate = null, bool IsReplay = false,
    bool MessageAllowanceConsumed = false, ModerationResult? ModeratedInput = null, string? SafetyText = null,
    AstraChoiceSelection? ChoiceSelection = null, HabitSetupAnswerContext? HabitDraft = null);

public sealed class AstraBlockAnswerService(
    IIssuedAstraBlockStore store,
    IUnitOfWork unitOfWork,
    IPayGateService payGate,
    IAgentOperationExecutor executor,
    IGenericRepository<User> users,
    IProductAnalytics analytics,
    IFeatureFlagService featureFlags,
    IPendingAgentOperationStore pendingOperations,
    IUserDateService dates,
    ILogger? logger = null,
    IPendingOperationChangePreviewer? previewer = null,
    TimeSpan? claimLifetime = null,
    Action<PreparedAstraBlockAnswer>? observePrepared = null,
    Action<ConversationToolCall>? observeTool = null,
    IPendingClarificationStore? clarifications = null)
{
    public async Task<Result<ChatResponse>> ExecuteAsync(ProcessUserChatCommand request,
        Func<PreparedAstraBlockAnswer, Task<Result<ChatResponse>>> produceResponse, CancellationToken ct,
        Func<string, CancellationToken, Task<Result<ModerationResult>>>? checkInput = null,
        Func<string, CancellationToken, Task<Result<ModerationResult>>>? checkChoiceInput = null)
    {
        var preparation = await PrepareAsync(request, ct, checkInput, checkChoiceInput);
        if (preparation.IsFailure)
            return preparation.PropagateError<ChatResponse>();
        var prepared = preparation.Value;
        if (prepared.ModeratedInput is not null)
            return await produceResponse(prepared);
        observePrepared?.Invoke(prepared);
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
            {
                var recovery = await CompleteCommittedAsync(request, prepared.AnsweredAtUtc!.Value);
                completed = recovery is not null;
                if (recovery is not null)
                    return Result.Success(recovery);
                if (result.ErrorCode != ErrorCodes.AiEmptyResponse)
                    return result;
                var user = await users.GetByIdAsync(request.UserId, ct);
                result = Result.Success(await ChatResponseNegotiation.RecoverEmptyAnswerAsync(request, ChatLocale.Resolve(request.ClientContext, user?.Language), null, ct));
            }
            using (ChatTurnTimer.Measure(ChatPhase.Save))
                await unitOfWork.ExecuteInTransactionAsync(async token =>
                {
                    await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                    if (!await store.CompleteAnswerAsync(request.BlockAnswer!.BlockId, request.UserId,
                            prepared.AnsweredAtUtc!.Value, JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions), token))
                        throw new InvalidOperationException("Block answer reservation was lost.");
                }, CancellationToken.None);
            completed = true;
            await CaptureAnsweredAsync(request, CancellationToken.None);
            return result;
        }
        catch
        {
            var recovery = await TryCompleteCommittedAsync(request, prepared.AnsweredAtUtc!.Value);
            if (recovery is not null)
            {
                completed = true;
                return Result.Success(recovery);
            }
            throw;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    await ReleaseAsync(request, prepared);
                }
                catch (Exception exception)
                {
                    (logger ?? NullLogger.Instance).LogWarning(exception,
                        "Block answer compensation failed for {BlockId}; recovery will retry after the claim expires", request.BlockAnswer!.BlockId);
                }
            }
        }
    }

    private async Task<ChatResponse?> TryCompleteCommittedAsync(ProcessUserChatCommand request, DateTime claim)
    {
        try
        {
            return await CompleteCommittedAsync(request, claim);
        }
        catch (Exception exception)
        {
            (logger ?? NullLogger.Instance).LogWarning(exception,
                "Block answer recovery failed for {BlockId}", request.BlockAnswer!.BlockId);
            return null;
        }
    }

    private async Task<ChatResponse?> CompleteCommittedAsync(ProcessUserChatCommand request, DateTime claim)
    {
        unitOfWork.ResetTracking();
        return await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
            var block = await store.GetAsync(request.BlockAnswer!.BlockId, request.UserId, token);
            if (block?.AnsweredAtUtc != claim || block.CommittedResponseJson is null)
                return null;
            if (block.ResponseJson is null)
                await store.CompleteAnswerAsync(block.Id, request.UserId, claim, block.CommittedResponseJson, token);
            return await ReadReplayAsync(await store.GetAsync(block.Id, request.UserId, token), request.BlockAnswer, request, token);
        }, CancellationToken.None);
    }

    private async Task ReleaseAsync(ProcessUserChatCommand request, PreparedAstraBlockAnswer prepared)
    {
        unitOfWork.ResetTracking();
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
            await ReleaseClaimAsync(request.BlockAnswer!.BlockId, request.UserId,
                prepared.AnsweredAtUtc!.Value, prepared.ReservedAllowanceDate, token);
        }, CancellationToken.None);
    }

    private async Task ReleaseClaimAsync(Guid blockId, Guid userId, DateTime claim, DateOnly? reservedDate, CancellationToken token)
    {
        if (!await store.ReleaseAnswerAsync(blockId, userId, claim, token) || reservedDate is null)
            return;
        var refund = await payGate.RefundAiMessage(userId, reservedDate.Value, unitOfWork, token);
        if (refund.IsFailure)
            throw new InvalidOperationException(refund.Error);
    }

    public async Task<Result<PreparedAstraBlockAnswer>> PrepareAsync(ProcessUserChatCommand request, CancellationToken ct,
        Func<string, CancellationToken, Task<Result<ModerationResult>>>? checkInput = null,
        Func<string, CancellationToken, Task<Result<ModerationResult>>>? checkChoiceInput = null)
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
                var block = await ReadCurrentBlockAsync(answer.BlockId, request.UserId, token);
                if (await ReadReplayAsync(block, answer, request, token) is { } replay)
                    return Result.Success(new PreparedAstraBlockAnswer(string.Empty, replay, IsReplay: true));
                var stateError = CheckState(block);
                if (stateError is not null)
                    return stateError;
                var definition = block!.ReadDefinition();
                var answerError = CheckAnswer(definition, answer);
                if (answerError is not null)
                    return answerError;
                var draftContext = await ReadDraftAsync(request.UserId, definition, answer, token);
                if (draftContext.IsFailure)
                    return draftContext.PropagateError<PreparedAstraBlockAnswer>();
                var isChoice = definition.Form != AstraBlockKinds.PickList;
                var message = BuildMessage(definition, answer);
                var safetyText = SafetyText(definition, answer, message);
                var crisis = await DetectCrisisTurnAsync(request.UserId, safetyText, token);
                var moderationCheck = isChoice ? checkChoiceInput ?? checkInput : checkInput;
                if (crisis == CrisisLocales.None && moderationCheck is not null)
                {
                    var inputCheck = await moderationCheck(safetyText, token);
                    if (inputCheck.IsFailure)
                        return inputCheck.PropagateError<PreparedAstraBlockAnswer>();
                    if (inputCheck.Value.Flagged)
                        return Result.Success(new PreparedAstraBlockAnswer(message, ModeratedInput: inputCheck.Value, SafetyText: safetyText));
                }
                var now = TimeProvider.System.GetUtcNow().UtcDateTime;
                var answeredAtUtc = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
                if (!await store.MarkAnsweredAsync(block.Id, request.UserId, answeredAtUtc, token))
                    return CheckState(await store.GetAsync(block.Id, request.UserId, token))
                        ?? Failure(ErrorCodes.AstraBlockAnswered);

                var user = await users.GetByIdAsync(request.UserId, token);
                ChatResponse? response = null;
                DateOnly? reservedAllowanceDate = null;
                var messageAllowanceConsumed = false;
                if (definition.Form == AstraBlockKinds.PickList && crisis == CrisisLocales.None)
                {
                    response = await BuildPickListResponseAsync(request, definition, answer, user, token);
                }
                else if (crisis == CrisisLocales.None)
                {
                    var reservation = await payGate.TryConsumeAiMessage(request.UserId, unitOfWork, token);
                    if (reservation.IsFailure)
                        throw new AnswerRejectedException(reservation.PropagateError<PreparedAstraBlockAnswer>());
                    messageAllowanceConsumed = true;
                    reservedAllowanceDate = (await users.GetByIdAsync(request.UserId, token))?.AiMessagesLocalDate;
                }
                using (ChatTurnTimer.Measure(ChatPhase.Save))
                    await store.RecordAnswerAsync(block.Id, request.UserId, answeredAtUtc,
                        IssuedAstraBlock.SerializeAnswer(answer), reservedAllowanceDate,
                        response is null ? null : JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions), token);
                return Result.Success(new PreparedAstraBlockAnswer(message, response, crisis,
                    answeredAtUtc, reservedAllowanceDate, MessageAllowanceConsumed: messageAllowanceConsumed, SafetyText: safetyText,
                    ChoiceSelection: definition.DraftClarification is null
                        ? AstraChoiceSelection.FromValidatedAnswer(definition, answer) : null, HabitDraft: draftContext.Value));
            }, ct);
        }
        catch (AnswerRejectedException exception)
        {
            return exception.Result;
        }
    }

    private async Task<IssuedAstraBlock?> ReadCurrentBlockAsync(Guid blockId, Guid userId, CancellationToken ct)
    {
        var block = await store.GetAsync(blockId, userId, ct);
        if (block is { AnsweredAtUtc: { } claim, AnswerJson: not null, ResponseJson: null }
            && claimLifetime is { } lifetime && claim + lifetime < TimeProvider.System.GetUtcNow().UtcDateTime)
        {
            if (block.CommittedResponseJson is { } committed)
                await store.CompleteAnswerAsync(block.Id, userId, claim, committed, ct);
            else
                await ReleaseClaimAsync(block.Id, userId, claim, block.ReservedAllowanceDate, ct);
            block = await store.GetAsync(blockId, userId, ct);
        }
        return block;
    }

    private async Task<Result<HabitSetupAnswerContext?>> ReadDraftAsync(Guid userId,
        AstraBlockDefinition definition, AstraBlockAnswer answer, CancellationToken ct)
    {
        if (definition.DraftClarification is not { } draft)
            return Result.Success<HabitSetupAnswerContext?>(null);
        var pending = clarifications is null ? null
            : await clarifications.GetForResolutionAsync(draft.ClarificationId, userId, ct);
        if (pending is null)
            return Failure(ErrorCodes.AstraBlockExpired).PropagateError<HabitSetupAnswerContext?>();
        var context = HabitSetupAnswerContext.FromValidatedAnswer(pending, definition, answer);
        return context is null
            ? Failure(ErrorCodes.AstraBlockInvalidSelection).PropagateError<HabitSetupAnswerContext?>()
            : Result.Success<HabitSetupAnswerContext?>(context);
    }

    private async Task<ChatResponse> BuildPickListResponseAsync(ProcessUserChatCommand request,
        AstraBlockDefinition definition, AstraBlockAnswer answer, User? user, CancellationToken token)
    {
        using var compositionPhase = ChatTurnTimer.Measure(ChatPhase.Composition);
        var chosen = definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).ToArray();
        var arguments = AskTool.BuildBulkArguments(definition.Action!, chosen);
        AgentExecuteOperationResponse preview;
        using (ChatTurnTimer.Measure(ChatPhase.ToolCall))
            preview = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
                request.UserId, "bulk_" + definition.Action,
                arguments, AgentExecutionSurface.Chat,
                request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
                CorrelationId: request.CorrelationId, IncludeChangePreview: true, PreviewOnly: true), token);
        observeTool?.Invoke(new ConversationToolCall("block-preview", "bulk_" + definition.Action,
            arguments.Clone(), preview.Operation.Status == AgentOperationStatus.Succeeded,
            preview.Operation.PolicyReason ?? (preview.Operation.Status == AgentOperationStatus.Succeeded
                ? null : preview.Operation.Status.ToString())));
        if (preview.Operation.Status != AgentOperationStatus.PendingConfirmation
            || preview.PendingOperation?.Items?.Count != chosen.Length)
            throw new AnswerRejectedException(Failure(ErrorCodes.AstraBlockPreviewUnavailable));
        var flags = await featureFlags.GetEnabledKeysForUserAsync(request.UserId, token);
        if (flags.Contains(FeatureFlagKeys.AstraBlockFirst))
            return ChatResponseNegotiation.ForClient(new ChatResponse(null, [],
                PendingOperations: [preview.PendingOperation], CorrelationId: request.CorrelationId, Parts: []),
                request.ClientContext, flags, ChatLocale.Resolve(request.ClientContext, user?.Language));
        var line = LocaleHelper.IsPortuguese(ChatLocale.Resolve(request.ClientContext, user?.Language)) ? "Nada foi salvo." : "Nothing has been saved.";
        return new ChatResponse(line, [], PendingOperations: [preview.PendingOperation],
            CorrelationId: request.CorrelationId,
            Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(line)] : null);
    }

    private async Task<ChatResponse?> ReadReplayAsync(IssuedAstraBlock? block, AstraBlockAnswer answer, ProcessUserChatCommand request, CancellationToken ct)
    {
        if (block is not { AnsweredAtUtc: not null, ResponseJson: not null } || !block.MatchesAnswer(answer))
            return null;
        var stored = JsonSerializer.Deserialize<ChatResponse>(block.ResponseJson, ChatStreamEvent.SerializerOptions)
            ?? throw new InvalidOperationException("Stored block has no response.");
        var userId = request.UserId;
        var flags = await featureFlags.GetEnabledKeysForUserAsync(userId, ct);
        var user = await users.GetByIdAsync(userId, ct);
        var userToday = await dates.GetUserTodayAsync(userId, ct);
        if (stored.PendingOperations is not { Count: > 0 } pending)
            return ChatResponseNegotiation.ForClient(stored, request.ClientContext, flags, ChatLocale.Resolve(request.ClientContext, user?.Language), user?.TimeZone, userToday);
        var current = new List<PendingAgentOperation>();
        foreach (var operation in pending)
        {
            if (!pendingOperations.IsAwaitingConfirmation(userId, operation.Id))
                continue;
            var execution = pendingOperations.GetExecution(userId, operation.Id);
            if (execution is null)
                continue;
            var preview = previewer is null ? null : await previewer.PreviewSafelyAsync(
                userId, execution.OperationId, execution.Arguments, logger ?? NullLogger.Instance, ct);
            if (preview is null && operation.PreviewFingerprint != execution.PreviewFingerprint)
                throw new AnswerRejectedException(Failure(ErrorCodes.AstraBlockAnswered));
            current.Add(preview is null ? operation : operation with
            {
                Items = preview.Items,
                Changes = preview.Changes,
                ChangeTargetCount = preview.ChangeTargetCount,
                PreviewFingerprint = preview.PreviewFingerprint
            });
        }
        return ChatResponseNegotiation.ForClient(stored with { PendingOperations = current }, request.ClientContext, flags,
            ChatLocale.Resolve(request.ClientContext, user?.Language), user?.TimeZone, userToday);
    }

    private static string BuildMessage(AstraBlockDefinition definition, AstraBlockAnswer answer)
    {
        if (definition.Form == AstraBlockKinds.Question)
            return JsonSerializer.Serialize(new
            {
                form = definition.Form,
                blockId = answer.BlockId,
                title = definition.Title,
                questions = definition.Questions,
                answers = answer.Answers
            }, ChatStreamEvent.SerializerOptions);
        var labels = definition.Form == AstraBlockKinds.Choice
            ? definition.Options.Where(option => answer.SelectedIds.Contains(option.Id)).Select(option => option.Label)
            : definition.Items.Where(item => answer.SelectedIds.Contains(item.Id)).Select(item => item.Label);
        var message = string.Join(", ", labels.Concat(string.IsNullOrWhiteSpace(answer.FreeText) ? [] : new[] { answer.FreeText }));
        return definition.Form == AstraBlockKinds.Choice ? definition.Title + "\n" + message : message;
    }

    private static string SafetyText(AstraBlockDefinition definition, AstraBlockAnswer answer, string message) =>
        definition.Form != AstraBlockKinds.Question ? message :
        string.Join("\n", definition.Questions!.Select(question =>
        {
            var value = answer.Answers!.Single(value => value.QuestionId == question.Id);
            var labels = question.Options?.Where(option => value.SelectedIds?.Contains(option.Id) == true)
                .Select(option => option.Label) ?? [];
            return question.Prompt + "\n" + string.Join(", ", labels.Append(value.FreeText ?? string.Empty));
        }));

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
                ["selected_count"] = answer.Answers?.Count ?? answer.SelectedIds.Count,
                ["used_free_text"] = !string.IsNullOrWhiteSpace(answer.FreeText) || answer.Answers?.Any(value => !string.IsNullOrWhiteSpace(value.FreeText)) == true,
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
        if (block.Form == AstraBlockKinds.Question)
            return AstraQuestionRules.IsValidAnswer(block, answer) ? null : Failure(ErrorCodes.AstraBlockInvalidSelection);
        if (answer.Answers is not null)
            return Failure(ErrorCodes.AstraBlockInvalidSelection);
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
