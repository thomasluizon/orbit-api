using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class NotificationController(
    OrbitDbContext dbContext) : ControllerBase
{
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth);

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();

        var existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);

        if (existing is not null)
        {
            if (existing.UserId == userId)
                return Ok();

            dbContext.PushSubscriptions.Remove(existing);
        }

        var result = PushSubscription.Create(userId, request.Endpoint, request.P256dh, request.Auth);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        await dbContext.PushSubscriptions.AddAsync(result.Value, ct);
        await dbContext.SaveChangesAsync(ct);

        return Ok();
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();

        var subscription = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == request.Endpoint, ct);

        if (subscription is not null)
        {
            dbContext.PushSubscriptions.Remove(subscription);
            await dbContext.SaveChangesAsync(ct);
        }

        return Ok();
    }
}
