using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Commands;

public record SendMarketingBroadcastCommand(
    string SubjectEn, string SubjectPt, string BodyHtmlEn, string BodyHtmlPt, string? TestEmail)
    : IRequest<Result<MarketingBroadcastResult>>;

public record MarketingBroadcastResult(int RecipientCount, bool WasTest);

public partial class SendMarketingBroadcastCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<MarketingContact> contactRepository,
    IBackgroundJobClient backgroundJobClient,
    IMarketingUnsubscribeTokenService unsubscribeTokenService,
    IGenericRepository<MarketingDelivery> deliveryRepository,
    IUnitOfWork unitOfWork,
    IOptions<MarketingSettings> marketingSettings,
    ILogger<SendMarketingBroadcastCommandHandler> logger,
    TimeProvider clock)
    : IRequestHandler<SendMarketingBroadcastCommand, Result<MarketingBroadcastResult>>
{
    private readonly MarketingSettings _settings = marketingSettings.Value;

    public async Task<Result<MarketingBroadcastResult>> Handle(SendMarketingBroadcastCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TestEmail))
        {
            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe?token={Uri.EscapeDataString(unsubscribeTokenService.CreateToken(Guid.Empty))}&lang=en";
            backgroundJobClient.Enqueue<SendMarketingEmailJob>(job => job.ExecuteAsync(request.TestEmail,
                request.SubjectEn, request.BodyHtmlEn, "en", url));
            LogPreviewSent(logger, request.SubjectEn);
            return Result.Success(new MarketingBroadcastResult(1, true));
        }

        var pageSize = Math.Clamp(_settings.BroadcastPageSize, 1, 500);
        var broadcastId = Guid.NewGuid();
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var pending = new List<MarketingDelivery>(pageSize);
        var total = 0;
        async Task Flush()
        {
            if (pending.Count == 0) return;
            await PersistAndDispatchAsync(pending, cancellationToken);
            unitOfWork.ResetTracking();
            pending.Clear();
        }
        async Task Add(Guid recipientId, string email, string language)
        {
            pending.Add(MarketingDelivery.Create(broadcastId, recipientId, email, language,
                request.SubjectEn, request.SubjectPt, request.BodyHtmlEn, request.BodyHtmlPt,
                nowAtUtc.AddMilliseconds((long)total * Math.Max(0, _settings.SendDelayMilliseconds))));
            total++;
            if (pending.Count == pageSize) await Flush();
        }

        Guid? cursor = null;
        while (true)
        {
            var users = await userRepository.FindIgnoringFiltersAsync(
                user => !user.IsDeactivated && user.MarketingEmailConsent == true
                    && (!cursor.HasValue || user.Id.CompareTo(cursor.Value) > 0),
                query => query.OrderBy(user => user.Id).Take(pageSize), cancellationToken);
            if (users.Count == 0) break;
            cursor = users[^1].Id;
            var emails = users.Select(user => user.Email.Trim().ToLowerInvariant()).ToList();
            var blocked = (await contactRepository.FindAsync(contact => emails.Contains(contact.Email)
                && (contact.UnsubscribedAtUtc != null || contact.SuppressedAtUtc != null), cancellationToken))
                .Select(contact => contact.Email).ToHashSet(StringComparer.Ordinal);
            foreach (var user in users.Where(user => !blocked.Contains(user.Email.Trim().ToLowerInvariant())))
                await Add(user.Id, user.Email, user.Language ?? "en");
            if (users.Count < pageSize) break;
        }
        cursor = null;
        while (true)
        {
            var contacts = await contactRepository.FindAsync(contact => contact.Source == "waitlist"
                && contact.UnsubscribedAtUtc == null && contact.SuppressedAtUtc == null
                && (!cursor.HasValue || contact.Id.CompareTo(cursor.Value) > 0),
                query => query.OrderBy(contact => contact.Id).Take(pageSize), cancellationToken);
            if (contacts.Count == 0) break;
            cursor = contacts[^1].Id;
            var emails = contacts.Select(contact => contact.Email).ToList();
            var userEmails = (await userRepository.FindIgnoringFiltersAsync(user => emails.Contains(user.Email.ToLower()),
                cancellationToken: cancellationToken)).Select(user => user.Email.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            foreach (var contact in contacts.Where(contact => !userEmails.Contains(contact.Email)))
                await Add(contact.Id, contact.Email, contact.Language);
            if (contacts.Count < pageSize) break;
        }
        await Flush();
        LogBroadcastQueued(logger, total, request.SubjectEn);
        return Result.Success(new MarketingBroadcastResult(total, false));
    }

    private async Task PersistAndDispatchAsync(List<MarketingDelivery> deliveries, CancellationToken cancellationToken)
    {
        foreach (var delivery in deliveries)
            await deliveryRepository.AddAsync(delivery, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await unitOfWork.ExecuteOnceInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync("marketing-dispatch", token);
            foreach (var delivery in deliveries)
            {
                await deliveryRepository.ReloadAsync(delivery, token);
                if (delivery.SentAtUtc is not null || delivery.BackgroundJobId is not null)
                    continue;
                var jobId = backgroundJobClient.Schedule<SendMarketingEmailJob>(job => job.ExecuteRecipientAsync(
                    delivery.BroadcastId, delivery.RecipientId, delivery.Email, delivery.Language),
                    new DateTimeOffset(delivery.NotBeforeAtUtc));
                if (jobId is not null)
                    delivery.RecordDispatch(jobId, clock.GetUtcNow().UtcDateTime);
                else
                    delivery.DeferRecovery(clock.GetUtcNow().UtcDateTime);
            }
            await unitOfWork.SaveChangesAsync(token);
        }, cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing broadcast preview sent; subject={Subject}")]
    private static partial void LogPreviewSent(ILogger logger, string subject);
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Marketing broadcast queued: {RecipientCount} recipients, subject={Subject}")]
    private static partial void LogBroadcastQueued(ILogger logger, int recipientCount, string subject);
}
