using System.Collections.Concurrent;
using Hangfire;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Jobs;

public sealed record MarketingRecipient(Guid UserId, string Email, string Language);

public sealed class SendMarketingBroadcastPageJob(IEmailService email, IMarketingUnsubscribeTokenService tokens,
    IOptions<MarketingSettings> options, TimeProvider clock, IMarketingBroadcastDeliveryStore deliveries)
{
    [Queue("marketing")]
    [DisableConcurrentExecution(600)]
    public async Task ExecuteAsync(Guid broadcastId, int pageNumber, SendMarketingBroadcastCommand content,
        MarketingRecipient[] recipients, CancellationToken cancellationToken)
    {
        if (broadcastId == Guid.Empty) throw new ArgumentException("Broadcast is required.", nameof(broadcastId));
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        if (recipients.Length > 500) throw new ArgumentException("A broadcast page contains at most 500 recipients.", nameof(recipients));
        if (recipients.Any(recipient => recipient.UserId == Guid.Empty)) throw new ArgumentException("Recipient is required.", nameof(recipients));
        var delivered = await deliveries.GetDeliveredRecipientIdsAsync(broadcastId, recipients.Select(recipient => recipient.UserId).ToArray(), cancellationToken);
        var settings = options.Value;
        var delay = TimeSpan.FromMilliseconds(Math.Max(settings.SendDelayMilliseconds, 1000d / Math.Clamp(settings.MaxSendsPerSecond, 1, 14)));
        using var pacing = new SemaphoreSlim(1, 1);
        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(recipients.DistinctBy(recipient => recipient.UserId).Where(recipient => !delivered.Contains(recipient.UserId)), new ParallelOptions
        { MaxDegreeOfParallelism = Math.Clamp(settings.BroadcastParallelism, 1, 4), CancellationToken = cancellationToken }, async (recipient, token) =>
        {
            await pacing.WaitAsync(token);
            try { await Task.Delay(delay, clock, token); }
            finally { pacing.Release(); }
            try
            {
                var portuguese = LocaleHelper.IsPortuguese(recipient.Language);
                var url = $"{settings.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe?token={Uri.EscapeDataString(tokens.CreateToken(recipient.UserId))}&lang={(portuguese ? "pt" : "en")}";
                await email.SendMarketingEmailAsync(recipient.Email, portuguese ? content.SubjectPt : content.SubjectEn,
                    portuguese ? content.BodyHtmlPt : content.BodyHtmlEn, recipient.Language, url, token);
                await deliveries.RecordDeliveredAsync(broadcastId, recipient.UserId, clock.GetUtcNow().UtcDateTime, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { failures.Enqueue(exception); }
        });
        if (!failures.IsEmpty) throw new AggregateException("Broadcast page delivery failed.", failures);
    }
}
