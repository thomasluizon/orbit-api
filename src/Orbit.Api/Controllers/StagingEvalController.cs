using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Authorization;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;

namespace Orbit.Api.Controllers;

[Authorize(Policy = StagingEvalRequirement.PolicyName, AuthenticationSchemes = "JwtBearer")]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/eval/pending-operations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StagingEvalController(ISender sender) : ControllerBase
{
    [HttpGet]
    [DistributedRateLimit("ai-operations")]
    [ProducesResponseType(typeof(EvalPendingOperationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPendingOperations(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetEvalPendingOperationsQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpDelete]
    [DistributedRateLimit("ai-operations")]
    [ProducesResponseType(typeof(ClearEvalPendingOperationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClearPendingOperations(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ClearEvalPendingOperationsCommand(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
