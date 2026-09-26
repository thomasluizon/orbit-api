using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Events;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/events")]
public sealed class EventsController(
    IAccountEventBus eventBus,
    EventTicketService ticketService,
    IHostApplicationLifetime hostLifetime) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(30);

    [HttpPost("ticket")]
    [Authorize(AuthenticationSchemes = "JwtBearer")]
    [ProducesResponseType<EventTicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<EventTicketResponse> CreateTicket()
    {
        try
        {
            var (ticket, expiresAtUtc) = ticketService.Create(User);
            return Ok(new EventTicketResponse(ticket, expiresAtUtc));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    [HttpGet]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Stream(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("orbit_session_id"), out var sessionId))
            return Unauthorized();

        DateTimeOffset tokenExpiresAtUtc;
        try
        {
            tokenExpiresAtUtc = EventTicketService.GetStreamExpiry(User);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
        {
            return Unauthorized();
        }

        var remaining = tokenExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return Unauthorized();

        var lastEventId = Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(lastEventId))
            lastEventId = null;
        if (!eventBus.TrySubscribe(HttpContext.GetUserId(), sessionId, lastEventId, out var subscription)
            || subscription is null)
            return StatusCode(StatusCodes.Status429TooManyRequests);

        using (subscription.Lease)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, hostLifetime.ApplicationStopping, subscription.SessionClosed))
        {
            lifetime.CancelAfter(remaining < MaxLifetime ? remaining : MaxLifetime);
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache, no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                await WriteEventAsync("ready", new { connectionId = subscription.ConnectionId }, null, lifetime.Token);
                var previousId = subscription.Resync ? null : lastEventId;
                if (subscription.Resync)
                    await WriteEventAsync("resync", new AccountEventPayload(1, []), null, lifetime.Token);

                foreach (var item in subscription.Replay)
                {
                    await WritePublishedAsync(item, previousId, lifetime.Token);
                    previousId = item.Id;
                }

                while (!lifetime.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    heartbeat.CancelAfter(HeartbeatInterval);
                    bool hasEvent;
                    try
                    {
                        hasEvent = await subscription.Reader.WaitToReadAsync(heartbeat.Token);
                    }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                    {
                        await Response.WriteAsync(": heartbeat\n\n", lifetime.Token);
                        await Response.Body.FlushAsync(lifetime.Token);
                        continue;
                    }

                    if (!hasEvent)
                        break;
                    while (subscription.Reader.TryRead(out var item))
                    {
                        await WritePublishedAsync(item, previousId, lifetime.Token);
                        previousId = item.Id;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        return new EmptyResult();
    }

    private async Task WritePublishedAsync(PublishedAccountEvent item, string? previousId, CancellationToken cancellationToken)
    {
        var hasGap = previousId is not null && !IsNext(previousId, item.Id);
        await WriteEventAsync(hasGap ? "resync" : item.Type,
            hasGap ? new AccountEventPayload(1, []) : item.Data,
            item.Id, cancellationToken);
    }

    private static bool IsNext(string previousId, string nextId)
    {
        var previousParts = previousId.Split('.', 2);
        var nextParts = nextId.Split('.', 2);
        return previousParts.Length == 2 && nextParts.Length == 2
            && previousParts[0] == nextParts[0]
            && long.TryParse(previousParts[1], out var previous)
            && long.TryParse(nextParts[1], out var next)
            && next == previous + 1;
    }

    private async Task WriteEventAsync(string type, object payload, string? id, CancellationToken cancellationToken)
    {
        if (id is not null)
            await Response.WriteAsync($"id: {id}\n", cancellationToken);
        await Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}

public sealed record EventTicketResponse(string Ticket, DateTime ExpiresAtUtc);
