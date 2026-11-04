using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Authorization;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Api.Streaming;
using Orbit.Application.Profile.Queries;

namespace Orbit.Api.Controllers;

[Authorize(Policy = EvalAccountRequirement.PolicyName, AuthenticationSchemes = "JwtBearer")]
[ApiController]
[Tags("StagingEval")]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/eval/snapshot")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EvalSnapshotController(ISender sender) : ControllerBase
{
    [HttpGet]
    [DistributedRateLimit("eval-snapshot")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetSnapshot(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new StreamUserDataQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => new StreamingJsonFileResult("orbit-eval-snapshot.json", value.WriteAsync));
    }
}
