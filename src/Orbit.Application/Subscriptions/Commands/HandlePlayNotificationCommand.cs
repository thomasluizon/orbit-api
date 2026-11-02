using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record HandlePlayNotificationCommand(string PushBody) : IRequest<Result>, IConcurrencyRetryable;

public partial class HandlePlayNotificationCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPlayBillingService playBilling,
    IPlayReferralCouponConsumer referralCouponConsumer,
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

        var messageId = decoded!.MessageId;
        if (string.IsNullOrEmpty(messageId)) return Result.Success();
        var claimRow = await ClaimAsync(messageId, cancellationToken);
        if (claimRow is null) return Result.Success();
        var stateCommitted = false;

        try
        {
            var state = await playBilling.VerifyAsync(notification.SubscriptionId, notification.PurchaseToken, cancellationToken);
            if (state is null) return Result.Success();
            var outcome = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                var user = await ResolveNotificationUserAsync(notification, state, token);
                if (user is null) return (User?)null;
                await unitOfWork.AcquireUserRowLockAsync(user.Id, token);
                await userRepository.ReloadAsync(user, token);
                var couponId = ApplySubscriptionState(user, state, notification.PurchaseToken,
                    notification.NotificationType, state.GrantsOrbitPro(_settings));
                if (!string.IsNullOrEmpty(couponId))
                {
                    claimRow.RecordConsumedCoupon(user.Id, couponId);
                    processedNotificationRepository.Update(claimRow);
                }
                await unitOfWork.SaveChangesAsync(token);
                return user;
            }, cancellationToken);
            stateCommitted = true;
            if (outcome is { } updatedUser)
            {
                await QueueCouponAsync(claimRow, cancellationToken);
                LogNotificationProcessed(logger, notification.NotificationType, updatedUser.Id, state.GrantsOrbitPro(_settings));
            }
            return Result.Success();
        }
        catch (BillingProviderException ex)
        {
            if (!stateCommitted) await ReleaseClaimAsync(messageId);
            LogVerifyFailed(logger, ex, notification.NotificationType);
            return Result.Failure(ErrorMessages.PlayNotificationVerificationFailed);
        }
        catch
        {
            if (!stateCommitted) await ReleaseClaimAsync(messageId);
            throw;
        }
    }

    private async Task<ProcessedPlayNotification?> ClaimAsync(string messageId, CancellationToken cancellationToken)
    {
        ProcessedPlayNotification? claimRow = null;
        var claimed = false;
        try
        {
            claimed = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"play-notification:{messageId}", token);
                if (await processedNotificationRepository.AnyAsync(p => p.MessageId == messageId, token)) return false;
                claimRow = ProcessedPlayNotification.Create(messageId);
                await processedNotificationRepository.AddAsync(claimRow, token);
                await unitOfWork.SaveChangesAsync(token);
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && DbUniqueViolation.IsUniqueViolation(ex))
        {
            unitOfWork.ResetTracking();
            if (!await processedNotificationRepository.AnyAsync(p => p.MessageId == messageId, cancellationToken)) throw;
        }
        if (claimed) return claimRow ?? throw new InvalidOperationException("Play notification claim was not recorded.");
        var existing = await processedNotificationRepository.FindOneTrackedAsync(p => p.MessageId == messageId,
            cancellationToken: cancellationToken);
        await QueueCouponAsync(existing, cancellationToken);
        LogDuplicateNotification(logger, messageId);
        return null;
    }

    private async Task QueueCouponAsync(ProcessedPlayNotification? claim, CancellationToken ct)
    {
        if (claim is not { CouponUserId: { } userId, ConsumedCouponId: { } couponId }) return;
        await referralCouponConsumer.CancelConsumedCouponAsync(userId, couponId, ct);
        claim.MarkCouponQueued();
        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task ReleaseClaimAsync(string messageId)
    {
        unitOfWork.ResetTracking();
        await processedNotificationRepository.DeleteAsync(p => p.MessageId == messageId, CancellationToken.None);
    }

    private async Task<User?> ResolveNotificationUserAsync(
        SubscriptionNotification notification, PlaySubscriptionState state, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(
            u => u.PlayPurchaseToken == notification.PurchaseToken, cancellationToken);

        if (user is null && !string.IsNullOrEmpty(state.LinkedPurchaseToken))
        {
            user = await userRepository.FindOneTrackedIgnoringFiltersAsync(
                u => u.PlayPurchaseToken == state.LinkedPurchaseToken, cancellationToken);
        }

        if (user is null)
        {
            LogUserNotFound(logger, notification.NotificationType);
            return null;
        }

        if (Guid.TryParse(state.ObfuscatedAccountId, out var purchaseAccountId) && purchaseAccountId != user.Id)
        {
            LogAccountMismatch(logger, user.Id);
            return null;
        }

        return user;
    }

    private string? ApplySubscriptionState(
        User user, PlaySubscriptionState state, string purchaseToken, int notificationType, bool grantsPro)
    {
        if (!grantsPro)
        {
            user.CancelPlaySubscription(GetPlayLapseReason(notificationType, user.SubscriptionLapseReason));
            return null;
        }

        var consumedCouponId = referralCouponConsumer.ConsumeOnNewPurchase(user, state, purchaseToken);
        user.SetPlaySubscription(purchaseToken, state.ExpiresAt, state.Interval, state.IsInGracePeriod);

        if (notificationType == 3 && !state.IsInGracePeriod)
            user.RecordSubscriptionLapseReason(
                SubscriptionSource.GooglePlay, SubscriptionLapseReason.Canceled);

        return consumedCouponId;
    }

    private static SubscriptionLapseReason GetPlayLapseReason(
        int notificationType, SubscriptionLapseReason? existingReason) => notificationType switch
        {
            5 => SubscriptionLapseReason.PaymentFailed,
            3 => existingReason == SubscriptionLapseReason.PaymentFailed
                ? SubscriptionLapseReason.PaymentFailed
                : SubscriptionLapseReason.Canceled,
            13 => existingReason ?? SubscriptionLapseReason.Expired,
            _ => SubscriptionLapseReason.Expired,
        };

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

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Play RTDN account mismatch for user {UserId}; skipping")]
    private static partial void LogAccountMismatch(ILogger logger, Guid userId);


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
