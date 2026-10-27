using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Api.Hosting;
using Orbit.Api.Middleware;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Idempotency;

public sealed class ChatTurnStream(
    IChatTurnStore store, IMediator mediator, ShutdownDrain drain, IOptions<ShutdownDrainOptions> options,
    IDistributedRateLimitService limits, ILogger<ChatTurnStream> logger)
{
    public async Task<IActionResult> RunAsync(ProcessUserChatCommand command, HttpContext http, string key, CancellationToken transportToken)
    {
        ChatErrorLocalization.SetLocale(http, command.ClientContext?.Locale);
        await new ChatTurnKeyValidator().ValidateAndThrowAsync(key, transportToken);
        await new ProcessUserChatCommandValidator().ValidateAndThrowAsync(command, transportToken);
        var candidate = ChatTurn.Create(command.UserId, ChatTurnIdentity.HashKey(key), ChatTurnIdentity.Fingerprint(command),
            TimeProvider.System.GetUtcNow().UtcDateTime.Add(options.Value.Timeout));
        var (turn, claimed) = await store.ClaimAsync(candidate, transportToken);
        if (turn.Fingerprint != candidate.Fingerprint)
            return new ConflictObjectResult(new { code = "CHAT_TURN_KEY_CONFLICT", error = await ChatErrorLocalization.MessageAsync(http, "CHAT_TURN_KEY_CONFLICT", "The turn key belongs to another payload.") });

        var connected = true;
        async Task Write(string json)
        {
            if (!connected)
                return;
            try
            {
                await http.Response.WriteAsync($"data: {json}\n\n", transportToken);
                await http.Response.Body.FlushAsync(transportToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                connected = false;
            }
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await Write((ChatStreamEvent.Started() with
        {
            TurnId = turn.Id,
            State = turn.TerminalEvent is null ? "running" : "completed",
            Replayed = !claimed
        }).ToJson());

        if (!claimed)
        {
            while (turn.TerminalEvent is null && turn.ExpiresAtUtc > TimeProvider.System.GetUtcNow().UtcDateTime)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), transportToken);
                turn = await store.ReadAsync(command.UserId, turn.Id, transportToken);
            }
            await Write(turn.TerminalEvent ?? (await InterruptedAsync(http, turn.Id)).ToJson());
            return new EmptyResult();
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(drain.ExecutionDeadline);
        execution.CancelAfter(options.Value.Timeout);
        var partition = $"user:{command.UserId}";
        DateTime? leaseEndAtUtc = null;
        ChatStreamEvent terminal;
        try
        {
            var lease = await limits.TryAcquireLeaseAsync(ConcurrentChatLimitFilter.PolicyName, partition,
                AppConstants.MaxConcurrentChatRequestsPerUser, TimeSpan.FromMinutes(5), execution.Token);
            if (!lease.Allowed)
                terminal = await ChatErrorLocalization.FailureAsync(http, 429, ErrorMessages.TooManyRequests.Code, ErrorMessages.TooManyRequests.Message);
            else
            {
                leaseEndAtUtc = lease.WindowEndsAtUtc;
                var result = await mediator.Send(command with
                {
                    TurnId = turn.Id,
                    StreamSink = command.BlockAnswer is null ? streamEvent => Write(streamEvent.ToJson()) : null
                }, execution.Token);
                terminal = result.IsSuccess ? ChatStreamEvent.Final(result.Value)
                    : await ChatErrorLocalization.FailureAsync(http, result.ResolveErrorStatus(), result.ErrorCode, result.Error, result.ErrorArgs);
            }
        }
        catch (ValidationException)
        {
            terminal = await ChatErrorLocalization.FailureAsync(http, 400, ErrorCodes.ValidationError, ErrorMessages.ValidationError.Message);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            terminal = await InterruptedAsync(http, turn.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Chat turn {TurnId} failed", turn.Id);
            terminal = await ChatErrorLocalization.FailureAsync(http, 500, ErrorMessages.AiUnavailable.Code, ErrorMessages.AiUnavailable.Message);
        }

        terminal = terminal with { TurnId = turn.Id, State = terminal.State ?? "completed" };
        var json = terminal.ToJson();
        try
        {
            using var persistenceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.CompleteAsync(command.UserId, turn.Id, json, persistenceDeadline.Token);
        }
        finally
        {
            if (leaseEndAtUtc is { } leaseEnd)
                await limits.ReleaseLeaseAsync(ConcurrentChatLimitFilter.PolicyName, partition, leaseEnd, CancellationToken.None);
        }
        await Write(json);
        return new EmptyResult();
    }

    private static async Task<ChatStreamEvent> InterruptedAsync(HttpContext http, Guid turnId) =>
        (await ChatErrorLocalization.FailureAsync(http, 503, "CHAT_TURN_INTERRUPTED",
            "The turn was interrupted. Its effects may be partial; do not resend it with another key.")) with
        { TurnId = turnId, State = "interrupted" };
}
