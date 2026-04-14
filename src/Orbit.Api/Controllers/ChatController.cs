using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[DistributedRateLimit("chat")]
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
        CancellationToken cancellationToken,
        [FromForm] string? clientContext = null,
        [FromForm] string? confirmationToken = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > AppConstants.MaxChatMessageLength)
            return BadRequest(new { error = $"Message must be between 1 and {AppConstants.MaxChatMessageLength} characters" });

        var (imageData, imageMimeType, imageError) = await ProcessImageAsync(image, cancellationToken);
        if (imageError is not null)
            return imageError;

        var (chatHistory, historyError) = ParseChatHistory(history);
        if (historyError is not null)
            return historyError;

        var (parsedClientContext, clientContextError) = ParseClientContext(clientContext);
        if (clientContextError is not null)
            return clientContextError;

        var command = new ProcessUserChatCommand(
            HttpContext.GetUserId(),
            message,
            imageData,
            imageMimeType,
            chatHistory,
            parsedClientContext,
            confirmationToken,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            HttpContext.User.IsReadOnlyCredential(),
            HttpContext.TraceIdentifier);

        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    private async Task<(byte[]? Data, string? MimeType, IActionResult? Error)> ProcessImageAsync(
        IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null)
            return (null, null, null);

        var validationResult = await imageValidation.ValidateAsync(image);
        if (validationResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogChatImageValidationFailed(logger, validationResult.Error);
            return (null, null, BadRequest(new { error = validationResult.Error }));
        }

        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, cancellationToken);
        return (ms.ToArray(), validationResult.Value.MimeType, null);
    }

    private (List<ChatHistoryMessage>? History, IActionResult? Error) ParseChatHistory(string? history)
    {
        if (string.IsNullOrWhiteSpace(history))
            return (null, null);

        if (history.Length > AppConstants.MaxChatHistoryLength)
            return (null, BadRequest(new { error = "Chat history too large" }));

        try
        {
            var chatHistory = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(history, ChatHistoryJsonOptions);
            if (chatHistory is null)
                return (null, BadRequest(new { error = "Invalid chat history format" }));

            if (chatHistory.Count > AppConstants.MaxChatHistoryMessages)
                return (null, BadRequest(new { error = "Chat history too large" }));

            var normalizedHistory = new List<ChatHistoryMessage>(chatHistory.Count);

            foreach (var item in chatHistory)
            {
                var normalizedRole = ChatHistoryMessage.NormalizeRole(item.Role);
                if (normalizedRole is null ||
                    string.IsNullOrWhiteSpace(item.Content) ||
                    item.Content.Length > AppConstants.MaxChatHistoryMessageLength)
                {
                    return (null, BadRequest(new { error = "Invalid chat history format" }));
                }

                normalizedHistory.Add(new ChatHistoryMessage(normalizedRole, item.Content));
            }

            return (normalizedHistory, null);
        }
        catch (JsonException ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogChatHistoryParseFailed(logger, ex, ex.Message);
            return (null, BadRequest(new { error = "Invalid chat history format" }));
        }
    }

    private (AgentClientContext? Context, IActionResult? Error) ParseClientContext(string? clientContext)
    {
        if (string.IsNullOrWhiteSpace(clientContext))
            return (null, null);

        try
        {
            var parsed = JsonSerializer.Deserialize<AgentClientContext>(clientContext, ChatHistoryJsonOptions);
            return parsed is null
                ? (null, BadRequest(new { error = "Invalid client context format" }))
                : (parsed, null);
        }
        catch (JsonException ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogClientContextParseFailed(logger, ex, ex.Message);
            return (null, BadRequest(new { error = "Invalid client context format" }));
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Chat image validation failed: {Error}")]
    private static partial void LogChatImageValidationFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Chat history parse failed: {Error}")]
    private static partial void LogChatHistoryParseFailed(ILogger logger, Exception ex, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Chat client context parse failed: {Error}")]
    private static partial void LogClientContextParseFailed(ILogger logger, Exception ex, string error);

}
