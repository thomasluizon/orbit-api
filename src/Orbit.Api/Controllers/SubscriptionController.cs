using Google.Apis.Auth;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/subscriptions")]
[Authorize]
public partial class SubscriptionController(
    IMediator mediator,
    IOptions<GooglePlaySettings> googlePlaySettings,
    ILogger<SubscriptionController> logger) : ControllerBase
{
    [HttpPost("checkout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateCheckoutRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateCheckoutCommand(
            HttpContext.GetUserId(),
            request.Interval,
            HttpContext.GetClientCountryCode(),
            HttpContext.GetClientIpAddress());
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
        var query = new GetPlansQuery(
            HttpContext.GetUserId(),
            HttpContext.GetClientCountryCode(),
            HttpContext.GetClientIpAddress());
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

#pragma warning disable S6932 // Raw Request.Body and Request.Headers needed for Stripe webhook signature verification
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
        if (logger.IsEnabled(LogLevel.Error))
            LogWebhookProcessingFailed(logger, result.Error);
        return StatusCode(500, new { error = result.Error });
    }
#pragma warning restore S6932

    [HttpPost("play/verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> VerifyPlayPurchase([FromBody] VerifyPlayPurchaseRequest request, CancellationToken cancellationToken)
    {
        var command = new VerifyPlayPurchaseCommand(
            HttpContext.GetUserId(),
            request.ProductId,
            request.PurchaseToken);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

#pragma warning disable S6932 // Raw Request.Body and Request.Headers needed for Play RTDN push verification
    [HttpPost("play/rtdn")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> HandlePlayNotification(CancellationToken cancellationToken)
    {
        var settings = googlePlaySettings.Value;
        if (!string.IsNullOrEmpty(settings.RtdnAudience)
            && !await IsValidPushTokenAsync(Request.Headers.Authorization.ToString(), settings))
        {
            return Unauthorized();
        }

        var body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(cancellationToken);
        var result = await mediator.Send(new HandlePlayNotificationCommand(body), cancellationToken);
        if (result.IsSuccess) return Ok();
        if (logger.IsEnabled(LogLevel.Error))
            LogPlayNotificationFailed(logger, result.Error);
        return StatusCode(500, new { error = result.Error });
    }
#pragma warning restore S6932

    private static async Task<bool> IsValidPushTokenAsync(string authorizationHeader, GooglePlaySettings settings)
    {
        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                authorizationHeader[bearerPrefix.Length..],
                new GoogleJsonWebSignature.ValidationSettings { Audience = [settings.RtdnAudience] });
            return payload.EmailVerified
                && string.Equals(payload.Email, settings.RtdnServiceAccountEmail, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidJwtException)
        {
            return false;
        }
    }

    public record CreateCheckoutRequest(string Interval);

    public record VerifyPlayPurchaseRequest(string ProductId, string PurchaseToken);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Webhook processing failed: {Error}")]
    private static partial void LogWebhookProcessingFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Play RTDN processing failed: {Error}")]
    private static partial void LogPlayNotificationFailed(ILogger logger, string? error);
}
