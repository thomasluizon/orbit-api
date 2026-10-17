using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.FeatureFlags.Queries;

namespace Orbit.Api.Controllers;

public partial class AdminController
{
    public record FeatureFlagRequest(string Action, string? Key = null, IReadOnlyList<string>? Emails = null, string? Description = null);

    [HttpGet("feature-flags")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListFeatureFlags(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ListFeatureFlagsQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("feature-flags")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ManageFeatureFlags([FromBody] FeatureFlagRequest request, CancellationToken cancellationToken)
    {
        if (request.Action == FeatureFlagActions.List)
            return await ListFeatureFlags(cancellationToken);

        var result = await mediator.Send(new ManageFeatureFlagsCommand(HttpContext.GetUserId(), request.Action,
            request.Key ?? string.Empty, request.Emails, request.Description), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
