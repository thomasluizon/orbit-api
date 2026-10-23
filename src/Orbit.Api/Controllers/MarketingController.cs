using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Pages;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/marketing")]
[AllowAnonymous]
public class MarketingController(IMediator mediator) : ControllerBase
{
    [HttpGet("unsubscribe")]
    [DistributedRateLimit("marketing-unsubscribe")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Unsubscribe(
        [FromQuery] string token,
        [FromQuery] string? lang,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnsubscribeMarketingCommand(token ?? ""), cancellationToken);
        var isPtBr = LocaleHelper.IsPortuguese(lang);

        return result.IsSuccess
            ? Content(ConfirmationPageHtml(isPtBr), "text/html")
            : new ContentResult
            {
                Content = InvalidPageHtml(isPtBr),
                ContentType = "text/html",
                StatusCode = StatusCodes.Status400BadRequest,
            };
    }

    [HttpPost("unsubscribe")]
    [DistributedRateLimit("marketing-unsubscribe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnsubscribeOneClick(
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnsubscribeMarketingCommand(token ?? ""), cancellationToken);
        return result.IsSuccess ? Ok() : BadRequest();
    }

    private static string ConfirmationPageHtml(bool isPtBr)
    {
        var (title, message) = isPtBr
            ? ("Inscrição cancelada", "Você não receberá mais e-mails de novidades do Orbit.")
            : ("You're unsubscribed", "You will no longer receive product-update emails from Orbit.");
        return MessagePage.Render(isPtBr ? "pt-BR" : "en", title, message);
    }

    private static string InvalidPageHtml(bool isPtBr)
    {
        var (title, message) = isPtBr
            ? ("Link inválido", "Este link para cancelar inscrição é inválido ou expirou.")
            : ("Invalid link", "This unsubscribe link is invalid or has expired.");
        return MessagePage.Render(isPtBr ? "pt-BR" : "en", title, message);
    }
}
