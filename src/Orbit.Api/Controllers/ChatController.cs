using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("chat")]
public partial class ChatController(IMediator mediator, IImageValidationService imageValidation, ILogger<ChatController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions ChatHistoryJsonOptions = new() { PropertyNameCaseInsensitive = true };
    [HttpPost]
    [RequestSizeLimit(10_485_760)] // 10MB
    [RequestFormLimits(MultipartBodyLengthLimit = 10_485_760)] // 10MB
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
                LogChatImageValidationFailed(logger, validationResult.Error);
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
            if (history.Length > AppConstants.MaxChatHistoryLength)
            {
                return BadRequest(new { error = "Chat history too large" });
            }

            try
            {
                chatHistory = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(history, ChatHistoryJsonOptions);
            }
            catch (JsonException ex)
            {
                LogChatHistoryParseFailed(logger, ex, ex.Message);
                return BadRequest(new { error = "Invalid chat history format" });
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Chat image validation failed: {Error}")]
    private static partial void LogChatImageValidationFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Chat history parse failed: {Error}")]
    private static partial void LogChatHistoryParseFailed(ILogger logger, Exception ex, string error);

}
