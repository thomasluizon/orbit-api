using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ChatController(IMediator mediator, IImageValidationService imageValidation) : ControllerBase
{
    [HttpPost]
    [RequestFormLimits(MultipartBodyLengthLimit = 20_971_520)] // 20MB
    public async Task<IActionResult> ProcessChat(
        [FromForm] string message,
        [FromForm] string? history,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        byte[]? imageData = null;
        string? imageMimeType = null;

        if (image is not null)
        {
            var validationResult = await imageValidation.ValidateAsync(image);
            if (validationResult.IsFailure)
                return BadRequest(new { error = validationResult.Error });

            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, cancellationToken);
            imageData = ms.ToArray();
            imageMimeType = validationResult.Value.MimeType;
        }

        // Parse conversation history
        List<ChatHistoryMessage>? chatHistory = null;
        if (!string.IsNullOrWhiteSpace(history))
        {
            try
            {
                chatHistory = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(history, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                // Invalid history format, ignore
            }
        }

        var command = new ProcessUserChatCommand(
            HttpContext.GetUserId(),
            message,
            imageData,
            imageMimeType,
            chatHistory);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode == "PAY_GATE"
            ? StatusCode(403, new { error = result.Error, code = "PAY_GATE" })
            : BadRequest(new { error = result.Error });
    }
}
