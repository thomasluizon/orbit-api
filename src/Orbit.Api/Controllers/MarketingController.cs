using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
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
        return Page(title, message);
    }

    private static string InvalidPageHtml(bool isPtBr)
    {
        var (title, message) = isPtBr
            ? ("Link inválido", "Este link para cancelar inscrição é inválido ou expirou.")
            : ("Invalid link", "This unsubscribe link is invalid or has expired.");
        return Page(title, message);
    }

    private static string Page(string title, string message) =>
        $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><title>{title}</title></head>
        <body style="margin:0;background-color:#020618;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <div style="max-width:480px;margin:0 auto;padding:80px 24px;text-align:center;color:#F8FAFC;">
            <h1 style="font-size:22px;margin:0 0 12px;">{title}</h1>
            <p style="font-size:15px;line-height:1.5;color:#90A1B9;margin:0;">{message}</p>
          </div>
        </body>
        </html>
        """;
}
