using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
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

    private const long MaxAudioBytes = 26_214_400;

    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".m4a", ".mp4", ".mp3", ".wav", ".ogg", ".oga", ".mpeg", ".mpga", ".flac"
    };

    [HttpPost]
    [RequestSizeLimit(10_485_760)]    [RequestFormLimits(MultipartBodyLengthLimit = 10_485_760)]    [ProducesResponseType(StatusCodes.Status200OK)]
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
            return BadRequest(ErrorMessages.MessageTooLong.Format(AppConstants.MaxChatMessageLength).ToErrorBody());

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

    [HttpPost("stream")]
    [RequestSizeLimit(10_485_760)]    [RequestFormLimits(MultipartBodyLengthLimit = 10_485_760)]    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ProcessChatStream(
        [FromForm] string message,
        [FromForm] string? history,
        IFormFile? image,
        CancellationToken cancellationToken,
        [FromForm] string? clientContext = null,
        [FromForm] string? confirmationToken = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > AppConstants.MaxChatMessageLength)
            return BadRequest(ErrorMessages.MessageTooLong.Format(AppConstants.MaxChatMessageLength).ToErrorBody());

        var (imageData, imageMimeType, imageError) = await ProcessImageAsync(image, cancellationToken);
        if (imageError is not null)
            return imageError;

        var (chatHistory, historyError) = ParseChatHistory(history);
        if (historyError is not null)
            return historyError;

        var (parsedClientContext, clientContextError) = ParseClientContext(clientContext);
        if (clientContextError is not null)
            return clientContextError;

        await StartEventStreamAsync(cancellationToken);

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
            HttpContext.TraceIdentifier,
            streamEvent => WriteEventAsync(streamEvent, cancellationToken));

        await StreamCommandResultAsync(command, cancellationToken);
        return new EmptyResult();
    }

    [HttpPost("transcribe")]
    [RequestSizeLimit(MaxAudioBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAudioBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Transcribe(IFormFile? audio, CancellationToken cancellationToken)
    {
        var validationError = ValidateAudio(audio);
        if (validationError is not null)
            return validationError;

        using var ms = new MemoryStream();
        await audio!.CopyToAsync(ms, cancellationToken);

        var result = await mediator.Send(new TranscribeAudioCommand(ms.ToArray(), audio.FileName), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : result.ToErrorResult();
    }

    private static IActionResult? ValidateAudio(IFormFile? audio)
    {
        if (audio is null || audio.Length == 0)
            return new BadRequestObjectResult(ErrorMessages.AudioRequired.ToErrorBody());
        if (audio.Length > MaxAudioBytes)
            return new BadRequestObjectResult(ErrorMessages.AudioTooLarge.Format(MaxAudioBytes / (1024 * 1024)).ToErrorBody());
        var ext = Path.GetExtension(audio.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedAudioExtensions.Contains(ext))
            return new BadRequestObjectResult(ErrorMessages.AudioFormatNotAllowed.Format(ext).ToErrorBody());
        return null;
    }

    private async Task StartEventStreamAsync(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await WriteEventAsync(ChatStreamEvent.Started(), cancellationToken);
    }

    private async Task StreamCommandResultAsync(ProcessUserChatCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mediator.Send(command, cancellationToken);
            var finalEvent = result.IsSuccess
                ? ChatStreamEvent.Final(result.Value)
                : ToErrorEvent(result);
            await WriteEventAsync(finalEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ValidationException ex)
        {
            await WriteEventAsync(
                ChatStreamEvent.Failure(StatusCodes.Status400BadRequest, ex.Message),
                cancellationToken);
        }
        catch (Exception ex)
        {
            LogChatStreamFailed(logger, ex);
            await WriteEventAsync(
                ChatStreamEvent.Failure(
                    StatusCodes.Status500InternalServerError,
                    ErrorMessages.AiUnavailable.Message,
                    ErrorMessages.AiUnavailable.Code),
                cancellationToken);
        }
    }

    private static ChatStreamEvent ToErrorEvent(Result<ChatResponse> result)
    {
        return ChatStreamEvent.Failure(result.ResolveErrorStatus(), result.Error, result.ErrorCode);
    }

    private async Task WriteEventAsync(ChatStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"data: {streamEvent.ToJson()}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task<(byte[]? Data, string? MimeType, IActionResult? Error)> ProcessImageAsync(
        IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null)
            return (null, null, null);

        using var uploadStream = image.OpenReadStream();
        var validationResult = await imageValidation.ValidateAsync(uploadStream, image.FileName, image.Length);
        if (validationResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogChatImageValidationFailed(logger, validationResult.Error);
            return (null, null, validationResult.ToErrorResult());
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
            return (null, BadRequest(ErrorMessages.ChatHistoryTooLarge.ToErrorBody()));

        try
        {
            var chatHistory = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(history, ChatHistoryJsonOptions);
            if (chatHistory is null)
                return (null, BadRequest(ErrorMessages.InvalidChatHistory.ToErrorBody()));

            if (chatHistory.Count > AppConstants.MaxChatHistoryMessages)
                return (null, BadRequest(ErrorMessages.ChatHistoryTooLarge.ToErrorBody()));

            var normalizedHistory = new List<ChatHistoryMessage>(chatHistory.Count);

            foreach (var item in chatHistory)
            {
                var normalizedRole = ChatHistoryMessage.NormalizeRole(item.Role);
                if (normalizedRole is null ||
                    string.IsNullOrWhiteSpace(item.Content) ||
                    item.Content.Length > AppConstants.MaxChatHistoryMessageLength)
                {
                    return (null, BadRequest(ErrorMessages.InvalidChatHistory.ToErrorBody()));
                }

                normalizedHistory.Add(new ChatHistoryMessage(normalizedRole, item.Content));
            }

            return (normalizedHistory, null);
        }
        catch (JsonException ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogChatHistoryParseFailed(logger, ex, ex.Message);
            return (null, BadRequest(ErrorMessages.InvalidChatHistory.ToErrorBody()));
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
                ? (null, BadRequest(ErrorMessages.InvalidClientContext.ToErrorBody()))
                : (parsed, null);
        }
        catch (JsonException ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogClientContextParseFailed(logger, ex, ex.Message);
            return (null, BadRequest(ErrorMessages.InvalidClientContext.ToErrorBody()));
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Chat image validation failed: {Error}")]
    private static partial void LogChatImageValidationFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Chat history parse failed: {Error}")]
    private static partial void LogChatHistoryParseFailed(ILogger logger, Exception ex, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Chat client context parse failed: {Error}")]
    private static partial void LogClientContextParseFailed(ILogger logger, Exception ex, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Chat stream processing failed")]
    private static partial void LogChatStreamFailed(ILogger logger, Exception ex);

}
