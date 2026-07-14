using System.Net;
using System.Text;
using FirebaseAdmin.Messaging;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class PushNotificationService(
    OrbitDbContext dbContext,
    IOptions<VapidSettings> vapidSettings,
    ILogger<PushNotificationService> logger,
    HttpClient httpClient) : IPushNotificationService
{
    private readonly VapidSettings _settings = vapidSettings.Value;

    public async Task SendToUserAsync(
        Guid userId,
        string title,
        string body,
        string? url = null,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await dbContext.PushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0) return;

        var safeTitle = SanitizeForDelivery(title, MaxTitleBytes);
        var safeBody = SanitizeForDelivery(body, MaxBodyBytes);

        var fcmSubs = subscriptions.Where(s => s.Transport == PushTransport.Fcm).ToList();
        var webPushSubs = subscriptions.Where(s => s.Transport == PushTransport.WebPush).ToList();

        var staleSubscriptions = new List<Domain.Entities.PushSubscription>();

        if (fcmSubs.Count > 0)
            await SendFcm(fcmSubs, safeTitle, safeBody, url, staleSubscriptions, cancellationToken);

        if (webPushSubs.Count > 0)
            await SendWebPush(webPushSubs, safeTitle, safeBody, url, staleSubscriptions, cancellationToken);

        if (staleSubscriptions.Count > 0)
        {
            dbContext.PushSubscriptions.RemoveRange(staleSubscriptions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendFcm(
        List<Domain.Entities.PushSubscription> subs,
        string title, string body, string? url,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        if (FirebaseMessaging.DefaultInstance is null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogFcmNotInitialized(logger, subs.Count);
            return;
        }

        const int FcmBatchSize = 500;
        for (int offset = 0; offset < subs.Count; offset += FcmBatchSize)
        {
            var chunk = subs.Skip(offset).Take(FcmBatchSize).ToList();
            var messages = chunk.Select(s => new Message
            {
                Token = s.Endpoint,
                Notification = new Notification { Title = title, Body = body },
                Data = new Dictionary<string, string> { ["url"] = url ?? "/" }
            }).ToList();

            BatchResponse response;
            try
            {
                response = await FirebaseMessaging.DefaultInstance.SendEachAsync(messages, ct);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogFcmPushFailed(logger, ex, "batch");
                foreach (var sub in chunk)
                    await SendFcmToSubscription(sub, title, body, url, staleSubscriptions, ct);
                continue;
            }

            RecordBatchOutcomes(response, chunk, staleSubscriptions);
        }
    }

    private void RecordBatchOutcomes(
        BatchResponse response,
        List<Domain.Entities.PushSubscription> chunk,
        List<Domain.Entities.PushSubscription> staleSubscriptions)
    {
        for (int i = 0; i < response.Responses.Count; i++)
        {
            var resp = response.Responses[i];
            var sub = chunk[i];
            var tokenPreview = sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...";
            if (resp.IsSuccess) continue;

            if (resp.Exception is FirebaseMessagingException fme && IsStaleFcmError(fme.MessagingErrorCode))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    LogFcmTokenStale(logger, tokenPreview);
                staleSubscriptions.Add(sub);
            }
            else if (resp.Exception is not null && logger.IsEnabled(LogLevel.Warning))
            {
                LogFcmPushFailed(logger, resp.Exception, tokenPreview);
            }
        }
    }

    private async Task SendFcmToSubscription(
        Domain.Entities.PushSubscription sub,
        string title, string body, string? url,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        var tokenPreview = sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...";
        try
        {
            var message = new Message
            {
                Token = sub.Endpoint,
                Notification = new Notification { Title = title, Body = body },
                Data = new Dictionary<string, string> { ["url"] = url ?? "/" }
            };
            await FirebaseMessaging.DefaultInstance!.SendAsync(message, ct);
            if (logger.IsEnabled(LogLevel.Debug))
                LogFcmPushSent(logger, tokenPreview);
        }
        catch (FirebaseMessagingException ex) when (IsStaleFcmError(ex.MessagingErrorCode))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogFcmTokenStale(logger, tokenPreview);
            staleSubscriptions.Add(sub);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogFcmPushFailed(logger, ex, tokenPreview);
        }
    }

    private async Task SendWebPush(
        List<Domain.Entities.PushSubscription> subs,
        string title, string body, string? url,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        var client = new PushServiceClient(httpClient);
        client.DefaultAuthentication = new VapidAuthentication(
            _settings.PublicKey, _settings.PrivateKey)
        {
            Subject = _settings.Subject
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, url });
        var message = new PushMessage(payload) { TimeToLive = 3600 };

        foreach (var sub in subs)
            await SendWebPushToSubscription(client, sub, message, staleSubscriptions, ct);
    }

    private async Task SendWebPushToSubscription(
        PushServiceClient client,
        Domain.Entities.PushSubscription sub,
        PushMessage message,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        const int MaxRetries = 2;
        const int BaseDelayMs = 300;

        var pushSub = new Lib.Net.Http.WebPush.PushSubscription
        {
            Endpoint = sub.Endpoint,
            Keys = new Dictionary<string, string>
            {
                ["p256dh"] = sub.P256dh,
                ["auth"] = sub.Auth
            }
        };

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var outcome = await TryDeliverWebPushAsync(client, pushSub, sub, message, attempt, MaxRetries, ct);
            if (outcome == WebPushAttemptOutcome.Stale)
                staleSubscriptions.Add(sub);
            if (outcome != WebPushAttemptOutcome.Retry)
                return;

            await Task.Delay(BaseDelayMs << attempt, ct);
        }
    }

    private enum WebPushAttemptOutcome { Delivered, Stale, GaveUp, Retry }

    private async Task<WebPushAttemptOutcome> TryDeliverWebPushAsync(
        PushServiceClient client,
        Lib.Net.Http.WebPush.PushSubscription pushSub,
        Domain.Entities.PushSubscription sub,
        PushMessage message,
        int attempt,
        int maxRetries,
        CancellationToken ct)
    {
        try
        {
            await client.RequestPushMessageDeliveryAsync(pushSub, message, ct);
            if (logger.IsEnabled(LogLevel.Debug))
                LogWebPushSent(logger, sub.Endpoint);
            return WebPushAttemptOutcome.Delivered;
        }
        catch (PushServiceClientException ex)
            when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogWebPushSubscriptionGone(logger, sub.Endpoint);
            return WebPushAttemptOutcome.Stale;
        }
        catch (PushServiceClientException ex)
        {
            return ClassifyTransientPushFailure(ex, sub, attempt, maxRetries);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (attempt >= maxRetries)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogWebPushFailedGeneric(logger, ex, sub.Endpoint);
                return WebPushAttemptOutcome.GaveUp;
            }
            return WebPushAttemptOutcome.Retry;
        }
    }

    private WebPushAttemptOutcome ClassifyTransientPushFailure(
        PushServiceClientException ex,
        Domain.Entities.PushSubscription sub,
        int attempt,
        int maxRetries)
    {
        if (IsTransient(ex.StatusCode) && attempt < maxRetries)
            return WebPushAttemptOutcome.Retry;

        if (logger.IsEnabled(LogLevel.Warning))
            LogWebPushFailed(logger, sub.Endpoint, ex.StatusCode, ex.Message);
        return WebPushAttemptOutcome.GaveUp;
    }

    /// <summary>
    /// A push token is pruned only when FCM reports it permanently invalid: an unregistered device,
    /// a malformed token, or a sender-ID mismatch. Transient failures - rate limits
    /// (<see cref="MessagingErrorCode.QuotaExceeded"/>), upstream auth faults
    /// (<see cref="MessagingErrorCode.ThirdPartyAuthError"/>), and server errors
    /// (<see cref="MessagingErrorCode.Internal"/> / <see cref="MessagingErrorCode.Unavailable"/>) -
    /// keep the subscription so a later delivery can succeed, isolating one bad send from the batch.
    /// </summary>
    internal static bool IsStaleFcmError(MessagingErrorCode? code) =>
        code is MessagingErrorCode.Unregistered
            or MessagingErrorCode.InvalidArgument
            or MessagingErrorCode.SenderIdMismatch;

    private static bool IsTransient(HttpStatusCode? status) =>
        status is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    internal const int MaxTitleBytes = 256;
    internal const int MaxBodyBytes = 768;

    /// <summary>
    /// Strips control characters and truncates <paramref name="text"/> to a whole-rune UTF-8
    /// byte budget so neither the FCM 4KB message payload nor the Web Push 4096-octet ceiling
    /// (RFC 8291 §4 / RFC 8030 §7.2, https://www.rfc-editor.org/rfc/rfc8291) is exceeded once the
    /// Web Push JSON escapes non-ASCII to \uXXXX (up to ~3x byte expansion). Never splits a rune.
    /// </summary>
    internal static string SanitizeForDelivery(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var builder = new StringBuilder(text.Length);
        var byteCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes) break;
            byteCount += runeBytes;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "FCM is not initialized (Firebase credentials not configured). Skipping FCM push to {Count} subscription(s).")]
    private static partial void LogFcmNotInitialized(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "FCM push sent to {Token}")]
    private static partial void LogFcmPushSent(ILogger logger, string token);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "FCM token {Token} is stale, removing")]
    private static partial void LogFcmTokenStale(ILogger logger, string token);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to send FCM push to {Token}")]
    private static partial void LogFcmPushFailed(ILogger logger, Exception ex, string token);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Web push sent to {Endpoint}")]
    private static partial void LogWebPushSent(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Web push subscription {Endpoint} is gone, removing")]
    private static partial void LogWebPushSubscriptionGone(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to send web push to {Endpoint}: {Status} {Message}")]
    private static partial void LogWebPushFailed(ILogger logger, string endpoint, System.Net.HttpStatusCode? status, string message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to send web push to {Endpoint}")]
    private static partial void LogWebPushFailedGeneric(ILogger logger, Exception ex, string endpoint);

}