using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record HandlePlayNotificationCommand(string PushBody) : IRequest<Result>;

public partial class HandlePlayNotificationCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPlayBillingService playBilling,
    IGenericRepository<ProcessedPlayNotification> processedNotificationRepository,
    IOptions<GooglePlaySettings> playSettings,
    ILogger<HandlePlayNotificationCommandHandler> logger) : IRequestHandler<HandlePlayNotificationCommand, Result>
{
    private readonly GooglePlaySettings _settings = playSettings.Value;

    public async Task<Result> Handle(HandlePlayNotificationCommand request, CancellationToken cancellationToken)
    {
        DecodedPlayNotification? decoded;
        try
        {
            decoded = DecodeNotification(request.PushBody);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            LogMalformedNotification(logger, ex);
            return Result.Success();
        }

        var notification = decoded?.Notification;
        if (notification is null
            || string.IsNullOrEmpty(notification.PurchaseToken)
            || string.IsNullOrEmpty(notification.SubscriptionId))
            return Result.Success();

        if (!string.IsNullOrEmpty(decoded!.MessageId)
            && await processedNotificationRepository.AnyAsync(p => p.MessageId == decoded.MessageId, cancellationToken))
        {
            LogDuplicateNotification(logger, decoded.MessageId);
            return Result.Success();
        }

        PlaySubscriptionState? state;
        try
        {
            state = await playBilling.VerifyAsync(notification.SubscriptionId, notification.PurchaseToken, cancellationToken);
        }
        catch (BillingProviderException ex)
        {
            LogVerifyFailed(logger, ex, notification.NotificationType);
            return Result.Failure("Failed to verify Play notification");
        }

        if (state is null)
            return Result.Success();

        var user = await userRepository.FindOneTrackedAsync(
            u => u.PlayPurchaseToken == notification.PurchaseToken, cancellationToken: cancellationToken);

        if (user is null && !string.IsNullOrEmpty(state.LinkedPurchaseToken))
        {
            user = await userRepository.FindOneTrackedAsync(
                u => u.PlayPurchaseToken == state.LinkedPurchaseToken, cancellationToken: cancellationToken);
        }

        if (user is null)
        {
            LogUserNotFound(logger, notification.NotificationType);
            return Result.Success();
        }

        var grantsPro = state.GrantsOrbitPro(_settings);
        if (grantsPro)
            user.SetPlaySubscription(notification.PurchaseToken, state.ExpiresAt, state.Interval);
        else
            user.CancelPlaySubscription();

        if (!string.IsNullOrEmpty(decoded.MessageId))
            await processedNotificationRepository.AddAsync(ProcessedPlayNotification.Create(decoded.MessageId), cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (!string.IsNullOrEmpty(decoded.MessageId))
        {
            if (await processedNotificationRepository.AnyAsync(p => p.MessageId == decoded.MessageId, cancellationToken))
            {
                LogDuplicateNotification(logger, decoded.MessageId);
                return Result.Success();
            }
            throw;
        }

        LogNotificationProcessed(logger, notification.NotificationType, user.Id, grantsPro);
        return Result.Success();
    }

    private static DecodedPlayNotification? DecodeNotification(string pushBody)
    {
        var envelope = JsonSerializer.Deserialize<PubSubEnvelope>(pushBody);
        if (string.IsNullOrEmpty(envelope?.Message?.Data))
            return null;

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Message.Data));
        var developerNotification = JsonSerializer.Deserialize<DeveloperNotification>(decoded);
        return new DecodedPlayNotification(envelope.Message.MessageId, developerNotification?.SubscriptionNotification);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Discarding malformed Play RTDN push")]
    private static partial void LogMalformedNotification(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to verify Play RTDN notification type {NotificationType}")]
    private static partial void LogVerifyFailed(ILogger logger, Exception ex, int notificationType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "No user matched Play RTDN notification type {NotificationType}")]
    private static partial void LogUserNotFound(ILogger logger, int notificationType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Processed Play RTDN type {NotificationType} for user {UserId}, active={IsActive}")]
    private static partial void LogNotificationProcessed(ILogger logger, int notificationType, Guid userId, bool isActive);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Discarding already-processed Play RTDN message {MessageId}")]
    private static partial void LogDuplicateNotification(ILogger logger, string messageId);

    private sealed class PubSubEnvelope
    {
        [JsonPropertyName("message")] public PubSubMessage? Message { get; set; }
    }

    private sealed class PubSubMessage
    {
        [JsonPropertyName("data")] public string? Data { get; set; }
        [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    }

    private sealed record DecodedPlayNotification(string? MessageId, SubscriptionNotification? Notification);

    private sealed class DeveloperNotification
    {
        [JsonPropertyName("subscriptionNotification")] public SubscriptionNotification? SubscriptionNotification { get; set; }
    }

    private sealed class SubscriptionNotification
    {
        [JsonPropertyName("notificationType")] public int NotificationType { get; set; }
        [JsonPropertyName("purchaseToken")] public string? PurchaseToken { get; set; }
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }
    }
}
