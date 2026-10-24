using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Api.Hosting;
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
        await new ChatTurnKeyValidator().ValidateAndThrowAsync(key, transportToken);
        await new ProcessUserChatCommandValidator().ValidateAndThrowAsync(command, transportToken);
        var candidate = ChatTurn.Create(command.UserId, ChatTurnIdentity.HashKey(key), ChatTurnIdentity.Fingerprint(command),
            TimeProvider.System.GetUtcNow().UtcDateTime.Add(options.Value.Timeout));
        var (turn, claimed) = await store.ClaimAsync(candidate, transportToken);
        if (turn.Fingerprint != candidate.Fingerprint)
            return new ConflictObjectResult(new { code = "CHAT_TURN_KEY_CONFLICT", error = "The turn key belongs to another payload." });

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
            catch (Exception exception) when (exception is IOException || exception is OperationCanceledException && transportToken.IsCancellationRequested)
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
            await Write(turn.TerminalEvent ?? Interrupted(turn.Id).ToJson());
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
                terminal = ChatStreamEvent.Failure(429, ErrorMessages.TooManyRequests.Message, ErrorMessages.TooManyRequests.Code);
            else
            {
                leaseEndAtUtc = lease.WindowEndsAtUtc;
                var result = await mediator.Send(command with
                {
                    TurnId = turn.Id,
                    StreamSink = command.BlockAnswer is null ? streamEvent => Write(streamEvent.ToJson()) : null
                }, execution.Token);
                terminal = result.IsSuccess ? ChatStreamEvent.Final(result.Value)
                    : ChatStreamEvent.Failure(result.ResolveErrorStatus(), result.Error, result.ErrorCode);
            }
        }
        catch (ValidationException exception)
        {
            terminal = ChatStreamEvent.Failure(400, exception.Message);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            terminal = Interrupted(turn.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Chat turn {TurnId} failed", turn.Id);
            terminal = ChatStreamEvent.Failure(500, ErrorMessages.AiUnavailable.Message, ErrorMessages.AiUnavailable.Code);
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

    private static ChatStreamEvent Interrupted(Guid turnId) =>
        ChatStreamEvent.Failure(503, "The turn was interrupted. Its effects may be partial; do not resend it with another key.",
            "CHAT_TURN_INTERRUPTED") with
        { TurnId = turnId, State = "interrupted" };
}
