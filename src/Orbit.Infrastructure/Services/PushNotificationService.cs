using System.Collections.Concurrent;
using System.Linq.Expressions;
using Orbit.Domain.Models;
using System.Net;
using System.Text;
using FirebaseAdmin.Messaging;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class PushNotificationService(
    OrbitDbContext dbContext,
    IOptions<VapidSettings> vapidSettings,
    ILogger<PushNotificationService> logger,
    HttpClient httpClient,
    IApnsPushSender apnsSender,
    IWnsPushSender wnsSender,
    IOptions<PushDeliverySettings>? deliverySettings = null) : IPushNotificationService, IDisposable
{
    private readonly object _authenticationGate = new();
    private readonly PushDeliverySettings _deliverySettings = deliverySettings?.Value ?? new();
    private readonly Lazy<PushServiceClient> _webPush = new(() => new PushServiceClient(httpClient)
    {
        AutoRetryAfter = false,
        DefaultAuthentication = new VapidAuthentication(vapidSettings.Value.PublicKey, vapidSettings.Value.PrivateKey)
        {
            Subject = vapidSettings.Value.Subject
        }
    });

    private TimeSpan AttemptTimeout => TimeSpan.FromSeconds(Math.Clamp(_deliverySettings.AttemptTimeoutSeconds, 1, 10));
    private int DeliveryConcurrency => Math.Clamp(_deliverySettings.Concurrency, 1, 32);
    internal const string RepeatReminderCategoryId = "orbit-repeat-reminder";

    public async Task SendToUserAsync(
        Guid userId,
        string title,
        string body,
        string? url = null,
        CancellationToken cancellationToken = default) =>
        await TrySendToUserAsync(userId, title, body, url, cancellationToken);

    public async Task<bool> TrySendToUserAsync(
        Guid userId,
        string title,
        string body,
        string? url = null,
        CancellationToken cancellationToken = default) =>
        await SendCoreAsync(userId, title, body, url, cancellationToken, null);

    public async Task SendReminderToUserAsync(Guid userId, string title, string body,
        IReadOnlyDictionary<string, string> data, string? url = null, CancellationToken cancellationToken = default) =>
        await SendCoreAsync(userId, title, body, url, cancellationToken, data);

    public async Task SendManyAsync(IReadOnlyList<PushDelivery> deliveries, CancellationToken cancellationToken = default) =>
        await SendManyCoreAsync(deliveries, cancellationToken);

    private Task<bool> SendCoreAsync(Guid userId, string title, string body, string? url,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? data) =>
        SendManyCoreAsync([new(userId, title, body, url, data)], cancellationToken);

    private async Task<bool> SendManyCoreAsync(IReadOnlyList<PushDelivery> deliveries, CancellationToken ct)
    {
        if (deliveries.Count == 0) return false;
        var ids = deliveries.Select(d => d.UserId).Distinct().ToArray();
        var subscriptions = (await dbContext.PushSubscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.UserId)).ToListAsync(ct)).ToLookup(s => s.UserId);
        var sends = deliveries.SelectMany(d => subscriptions[d.UserId].Select(s => new PendingPush(s,
            d with { Title = SanitizeForDelivery(d.Title, MaxTitleBytes), Body = SanitizeForDelivery(d.Body, MaxBodyBytes) }))).ToList();
        var stale = new ConcurrentBag<Domain.Entities.PushSubscription>();
        var delivered = 0;
        var fcmTask = SendFcm(sends.Where(s => s.Subscription.Transport == PushTransport.Fcm).ToList(), stale, ct);
        await Parallel.ForEachAsync(sends.Where(s => s.Subscription.Transport != PushTransport.Fcm),
            new ParallelOptions { MaxDegreeOfParallelism = DeliveryConcurrency, CancellationToken = ct }, async (send, token) =>
        {
            var sub = send.Subscription;
            var d = send.Delivery;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(AttemptTimeout);
            try
            {
                bool success;
                switch (sub.Transport)
                {
                    case PushTransport.Apns:
                        var apns = await apnsSender.SendAsync(sub.Endpoint, d.Title, d.Body, d.Url, deadline.Token, sub.ApnsTopic, d.Data);
                        if (apns == ApnsDeliveryOutcome.Stale) stale.Add(sub);
                        success = apns == ApnsDeliveryOutcome.Delivered;
                        break;
                    case PushTransport.Wns:
                        var wns = await wnsSender.SendAsync(sub.Endpoint, CreateWebPushPayload(d.Title, d.Body, d.Url, d.Data), deadline.Token);
                        if (wns == WnsDeliveryOutcome.Stale) stale.Add(sub);
                        success = wns == WnsDeliveryOutcome.Delivered;
                        break;
                    default:
                        success = await SendWebPushToSubscription(_webPush.Value, sub,
                            new PushMessage(CreateWebPushPayload(d.Title, d.Body, d.Url, d.Data)) { TimeToLive = 3600 }, stale, token);
                        break;
                }
                if (success) Interlocked.Exchange(ref delivered, 1);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogWebPushFailedGeneric(logger, ex, sub.Endpoint);
            }
        });
        if (await fcmTask) delivered = 1;
        foreach (var group in stale.DistinctBy(s => s.Id).GroupBy(s => s.UserId))
        {
            Expression<Func<Domain.Entities.PushSubscription, bool>> guard = s => false;
            foreach (var entry in group)
            {
                var row = entry;
                Expression<Func<Domain.Entities.PushSubscription, bool>> matches = s =>
                    s.Id == row.Id && s.UserId == row.UserId && s.Endpoint == row.Endpoint
                    && s.P256dh == row.P256dh && s.Auth == row.Auth && s.ApnsTopic == row.ApnsTopic
                    && s.DeviceId == row.DeviceId && s.CreatedAtUtc == row.CreatedAtUtc;
                var body = new ReplaceParameter(matches.Parameters[0], guard.Parameters[0]).Visit(matches.Body)!;
                guard = Expression.Lambda<Func<Domain.Entities.PushSubscription, bool>>(Expression.OrElse(guard.Body, body), guard.Parameters);
            }
            await dbContext.PushSubscriptions.Where(guard).ExecuteDeleteAsync(ct);
        }
        return delivered != 0;
    }

    private sealed class ReplaceParameter(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : base.VisitParameter(node);
    }

    private sealed record PendingPush(Domain.Entities.PushSubscription Subscription, PushDelivery Delivery);

    internal virtual Task<BatchResponse> SendFcmBatchAsync(IReadOnlyList<Message> messages, CancellationToken ct) =>
        FirebaseMessaging.DefaultInstance!.SendEachAsync(messages, ct);

    private async Task<bool> SendFcm(List<PendingPush> sends,
        ConcurrentBag<Domain.Entities.PushSubscription> stale, CancellationToken ct)
    {
        if (sends.Count == 0) return false;
        var messaging = FirebaseMessaging.DefaultInstance;
        if (messaging is null)
        {
            if (logger.IsEnabled(LogLevel.Warning)) LogFcmNotInitialized(logger, sends.Count);
            return false;
        }
        var delivered = 0;
        foreach (var chunk in sends.Chunk(500))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(AttemptTimeout);
            try
            {
                var response = await SendFcmBatchAsync(chunk.Select(s => CreateFcmMessage(s.Subscription.Endpoint,
                    s.Delivery.Title, s.Delivery.Body, s.Delivery.Url, s.Delivery.Data)).ToList(), deadline.Token);
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < response.Responses.Count; i++)
                {
                    var result = response.Responses[i];
                    if (result.IsSuccess) delivered = 1;
                    else RecordFcmFailure(chunk[i].Subscription, result.Exception, stale);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                LogFcmPushFailed(logger, ex, "batch timeout");
            }
            catch (Exception ex)
            {
                LogFcmPushFailed(logger, ex, "batch");
                await Parallel.ForEachAsync(chunk,
                    new ParallelOptions { MaxDegreeOfParallelism = DeliveryConcurrency, CancellationToken = ct }, async (send, token) =>
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(AttemptTimeout);
                    try
                    {
                        var d = send.Delivery;
                        await messaging.SendAsync(CreateFcmMessage(send.Subscription.Endpoint, d.Title, d.Body, d.Url, d.Data), attempt.Token);
                        Interlocked.Exchange(ref delivered, 1);
                    }
                    catch (Exception failure) when (!token.IsCancellationRequested)
                    {
                        RecordFcmFailure(send.Subscription, failure, stale);
                    }
                });
            }
        }
        return delivered != 0;
    }

    private void RecordFcmFailure(Domain.Entities.PushSubscription sub, Exception? failure,
        ConcurrentBag<Domain.Entities.PushSubscription> stale)
    {
        if (failure is FirebaseMessagingException fme && IsStaleFcmError(fme.MessagingErrorCode))
        {
            stale.Add(sub);
            if (logger.IsEnabled(LogLevel.Debug)) LogFcmTokenStale(logger, sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)]);
        }
        else if (failure is not null && logger.IsEnabled(LogLevel.Warning))
            LogFcmPushFailed(logger, failure, sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)]);
    }

    private async Task<bool> SendWebPushToSubscription(
        PushServiceClient client,
        Domain.Entities.PushSubscription sub,
        PushMessage message,
        ConcurrentBag<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        const int MaxRetries = 2;
        const int BaseDelayMs = 300;

        if (!WebPushEndpointPolicy.IsAllowed(sub.Endpoint))
            return false;

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
            if (outcome == WebPushAttemptOutcome.Delivered)
                return true;
            if (outcome != WebPushAttemptOutcome.Retry)
                return false;

            await Task.Delay(BaseDelayMs << attempt, ct);
        }
        return false;
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(AttemptTimeout);
            Task delivery;
            lock (_authenticationGate)
                delivery = client.RequestPushMessageDeliveryAsync(pushSub, message, deadline.Token);
            await delivery;
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
            if (logger.IsEnabled(LogLevel.Warning)) LogWebPushFailedGeneric(logger, ex, sub.Endpoint);
            return WebPushAttemptOutcome.GaveUp;
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

    internal static bool IsStaleFcmError(MessagingErrorCode? code) =>
        code is MessagingErrorCode.Unregistered
            or MessagingErrorCode.SenderIdMismatch;

    private static bool IsTransient(HttpStatusCode? status) =>
        status is HttpStatusCode.TooManyRequests || (int?)status >= 500;

    internal static Message CreateFcmMessage(string token, string title, string body, string? url, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var data = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata);
        if (url is not null)
            data["url"] = url;

        if (data.ContainsKey("doneToken"))
        {
            data["title"] = title;
            data["message"] = body;
            data["categoryId"] = RepeatReminderCategoryId;
            data["channelId"] = "default";

            return new Message
            {
                Token = token,
                Android = new AndroidConfig { Priority = Priority.High },
                Data = data
            };
        }

        return new Message
        {
            Token = token,
            Notification = new Notification { Title = title, Body = body },
            Android = data.TryGetValue("tag", out var tag)
                ? new AndroidConfig { Notification = new AndroidNotification { Tag = tag } } : null,
            Data = data
        };
    }

    internal static string CreateWebPushPayload(string title, string body, string? url, IReadOnlyDictionary<string, string>? data = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["title"] = title,
            ["body"] = body
        };
        if (data is not null)
            foreach (var (key, value) in data)
                payload[key] = value;
        if (url is not null)
            payload["url"] = url;

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

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

    public void Dispose()
    {
        if (_webPush.IsValueCreated) _webPush.Value.DefaultAuthentication?.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "FCM is not initialized (Firebase credentials not configured). Skipping FCM push to {Count} subscription(s).")]
    private static partial void LogFcmNotInitialized(ILogger logger, int count);

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
