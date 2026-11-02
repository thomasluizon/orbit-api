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
    ILogger<HandlePlayNotificationCommandHandler> logger,
    TimeProvider? timeProvider = null) : IRequestHandler<HandlePlayNotificationCommand, Result>
{
    private readonly GooglePlaySettings _settings = playSettings.Value;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromMinutes(2);

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
        var claim = await ClaimAsync(messageId, cancellationToken);
        if (claim.InProgress) return Result.Failure(ErrorMessages.PlayNotificationVerificationFailed);
        var claimRow = claim.Row;
        if (claimRow is null) return Result.Success();
        var leaseId = claimRow.ProcessingLeaseId
            ?? throw new InvalidOperationException("The Play notification claim has no processing lease.");
        var stateCommitted = false;

        try
        {
            using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            processing.CancelAfter(TimeSpan.FromSeconds(90));
            var state = await playBilling.VerifyAsync(notification.SubscriptionId, notification.PurchaseToken, processing.Token);
            var outcome = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"play-notification:{messageId}", token);
                var current = await processedNotificationRepository.FindOneTrackedAsync(p => p.MessageId == messageId,
                    cancellationToken: token);
                if (current is null) throw new BillingProviderException("The Play notification processing lease was released.");
                await processedNotificationRepository.ReloadAsync(current, token);
                if (current.ProcessingLeaseId != leaseId)
                    throw new BillingProviderException("The Play notification processing lease was replaced.");
                claimRow = current;
                var user = state is null ? null : await ResolveNotificationUserAsync(notification, state, token);
                if (user is not null && state is not null)
                {
                    await unitOfWork.AcquireUserRowLockAsync(user.Id, token);
                    await userRepository.ReloadAsync(user, token);
                    var couponId = ApplySubscriptionState(user, state, notification.PurchaseToken,
                        notification.NotificationType, state.GrantsOrbitPro(_settings));
                    if (!string.IsNullOrEmpty(couponId))
                    {
                        claimRow.RecordConsumedCoupon(user.Id, couponId);
                        processedNotificationRepository.Update(claimRow);
                    }
                }
                claimRow.Complete(leaseId, _clock.GetUtcNow().UtcDateTime);
                await unitOfWork.SaveChangesAsync(token);
                return user;
            }, processing.Token);
            stateCommitted = true;
            if (outcome is { } updatedUser && state is not null)
            {
                await QueueCouponAsync(claimRow, cancellationToken);
                LogNotificationProcessed(logger, notification.NotificationType, updatedUser.Id, state.GrantsOrbitPro(_settings));
            }
            return Result.Success();
        }
        catch (BillingProviderException ex)
        {
            if (!stateCommitted) await ReleaseClaimAsync(messageId, leaseId);
            LogVerifyFailed(logger, ex, notification.NotificationType);
            return Result.Failure(ErrorMessages.PlayNotificationVerificationFailed);
        }
        catch
        {
            if (!stateCommitted) await ReleaseClaimAsync(messageId, leaseId);
            throw;
        }
    }

    private async Task<(ProcessedPlayNotification? Row, bool InProgress)> ClaimAsync(string messageId, CancellationToken cancellationToken)
    {
        ProcessedPlayNotification? claimRow = null;
        var claimed = false;
        var inProgress = false;
        try
        {
            claimed = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"play-notification:{messageId}", token);
                if (await processedNotificationRepository.AnyAsync(p => p.MessageId == messageId, token))
                {
                    claimRow = await processedNotificationRepository.FindOneTrackedAsync(p => p.MessageId == messageId,
                        cancellationToken: token);
                    if (claimRow is null) throw new InvalidOperationException("The Play notification claim disappeared.");
                    await processedNotificationRepository.ReloadAsync(claimRow, token);
                    if (claimRow.ProcessingLeaseId is null) return false;
                    if (!claimRow.TryReclaim(_clock.GetUtcNow().UtcDateTime, ClaimLeaseDuration))
                    {
                        inProgress = true;
                        return false;
                    }
                }
                else
                {
                    claimRow = ProcessedPlayNotification.Claim(messageId, _clock.GetUtcNow().UtcDateTime, ClaimLeaseDuration);
                    await processedNotificationRepository.AddAsync(claimRow, token);
                }
                await unitOfWork.SaveChangesAsync(token);
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && DbUniqueViolation.IsUniqueViolation(ex))
        {
            unitOfWork.ResetTracking();
            if (!await processedNotificationRepository.AnyAsync(p => p.MessageId == messageId, cancellationToken)) throw;
        }
        if (claimed) return (claimRow ?? throw new InvalidOperationException("Play notification claim was not recorded."), false);
        if (inProgress) return (null, true);
        var existing = await processedNotificationRepository.FindOneTrackedAsync(p => p.MessageId == messageId,
            cancellationToken: cancellationToken);
        if (existing?.ProcessingLeaseId is not null) return (null, true);
        await QueueCouponAsync(existing, cancellationToken);
        LogDuplicateNotification(logger, messageId);
        return (null, false);
    }

    private async Task QueueCouponAsync(ProcessedPlayNotification? claim, CancellationToken ct)
    {
        if (claim is not { CouponUserId: { } userId, ConsumedCouponId: { } couponId }) return;
        await referralCouponConsumer.CancelConsumedCouponAsync(userId, couponId, ct);
        claim.MarkCouponQueued();
        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task ReleaseClaimAsync(string messageId, Guid leaseId)
    {
        unitOfWork.ResetTracking();
        await processedNotificationRepository.DeleteAsync(p => p.MessageId == messageId && p.ProcessingLeaseId == leaseId, CancellationToken.None);
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
