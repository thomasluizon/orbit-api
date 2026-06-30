using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Orbit.Api.RateLimiting;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/u")]
public class PublicProfileController(IMediator mediator, IDistributedCache cache) : ControllerBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly DistributedCacheEntryOptions CacheEntryOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    [HttpGet("{slug}")]
    [AllowAnonymous]
    [DistributedRateLimit("public-profile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPublicProfile(string slug, CancellationToken cancellationToken)
    {
        var cacheKey = UpdatePublicProfileCommandHandler.CacheKeyPrefix + slug;

        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
            return Content(cached, "application/json");

        var result = await mediator.Send(new GetPublicProfileQuery(slug), cancellationToken);
        if (result.IsFailure)
            return NotFound();

        var json = JsonSerializer.Serialize(result.Value, SerializerOptions);
        await cache.SetStringAsync(cacheKey, json, CacheEntryOptions, cancellationToken);

        return Content(json, "application/json");
    }
}
