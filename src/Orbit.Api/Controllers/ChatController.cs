using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ChatController(IMediator mediator, IImageValidationService imageValidation, ILogger<ChatController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(10_485_760)] // 10MB
    [RequestFormLimits(MultipartBodyLengthLimit = 10_485_760)] // 10MB
    public async Task<IActionResult> ProcessChat(
        [FromForm] string message,
        [FromForm] string? history,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 4000)
            return BadRequest(new { error = "Message must be between 1 and 4000 characters" });

        byte[]? imageData = null;
        string? imageMimeType = null;

        if (image is not null)
        {
            var validationResult = await imageValidation.ValidateAsync(image);
            if (validationResult.IsFailure)
            {
                logger.LogWarning("Chat image validation failed: {Error}", validationResult.Error);
                return BadRequest(new { error = validationResult.Error });
            }

            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, cancellationToken);
            imageData = ms.ToArray();
            imageMimeType = validationResult.Value.MimeType;
        }

        // Parse conversation history
        List<ChatHistoryMessage>? chatHistory = null;
        if (!string.IsNullOrWhiteSpace(history))
        {
            if (history.Length > 50_000)
            {
                return BadRequest(new { error = "Chat history too large" });
            }

            try
            {
                chatHistory = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(history, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Chat history parse failed: {Error}", ex.Message);
            }
        }

        var command = new ProcessUserChatCommand(
            HttpContext.GetUserId(),
            message,
            imageData,
            imageMimeType,
            chatHistory);

        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }
}
