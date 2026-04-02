using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Subscriptions;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/subscriptions")]
[Authorize]
public class SubscriptionController(IMediator mediator, ILogger<SubscriptionController> logger) : ControllerBase
{
    [HttpPost("checkout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateCheckoutRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateCheckoutCommand(HttpContext.GetUserId(), request.Interval, HttpContext.Connection.RemoteIpAddress?.ToString());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("portal")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePortal(CancellationToken cancellationToken)
    {
        var command = new CreatePortalSessionCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var query = new GetSubscriptionStatusQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("billing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetBillingDetails(CancellationToken cancellationToken)
    {
        var query = new GetBillingDetailsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("plans")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPlans(CancellationToken cancellationToken)
    {
        var query = new GetPlansQuery(HttpContext.GetUserId(), HttpContext.Connection.RemoteIpAddress?.ToString());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("ad-reward")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ClaimAdReward(CancellationToken cancellationToken)
    {
        var command = new ClaimAdRewardCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> HandleWebhook(CancellationToken cancellationToken)
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["Stripe-Signature"].ToString();
        var command = new HandleWebhookCommand(json, signature);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess) return Ok();
        logger.LogError("Webhook processing failed: {Error}", result.Error);
        return StatusCode(500, new { error = result.Error });
    }

    public record CreateCheckoutRequest(string Interval);
}
