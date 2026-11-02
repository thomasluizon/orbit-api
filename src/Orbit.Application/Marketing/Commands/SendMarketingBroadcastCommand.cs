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
    string SubjectEn,
    string SubjectPt,
    string BodyHtmlEn,
    string BodyHtmlPt,
    string? TestEmail) : IRequest<Result<MarketingBroadcastResult>>;

public record MarketingBroadcastResult(int RecipientCount, bool WasTest);

public partial class SendMarketingBroadcastCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<MarketingContact> contactRepository,
    IBackgroundJobClient backgroundJobClient,
    IMarketingUnsubscribeTokenService unsubscribeTokenService,
    IGenericRepository<MarketingDelivery> deliveryRepository,
    IUnitOfWork unitOfWork,
    IOptions<MarketingSettings> marketingSettings,
    ILogger<SendMarketingBroadcastCommandHandler> logger)
    : IRequestHandler<SendMarketingBroadcastCommand, Result<MarketingBroadcastResult>>
{
    private readonly MarketingSettings _settings = marketingSettings.Value;

    public async Task<Result<MarketingBroadcastResult>> Handle(
        SendMarketingBroadcastCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TestEmail))
        {
            string testEmail = request.TestEmail;
            var preview = RenderFor(request, "en");
            var previewUrl = BuildUnsubscribeUrl(unsubscribeTokenService.CreateToken(Guid.Empty), "en");
            backgroundJobClient.Enqueue<SendMarketingEmailJob>(
                job => job.ExecuteAsync(testEmail, preview.Subject, preview.BodyHtml, "en", previewUrl));

            LogPreviewSent(logger, request.SubjectEn);
            return Result.Success(new MarketingBroadcastResult(RecipientCount: 1, WasTest: true));
        }

        var users = await userRepository.FindIgnoringFiltersAsync(user => true, cancellationToken: cancellationToken);
        var contacts = await contactRepository.GetAllAsync(cancellationToken);

        var userEmails = users.Select(user => user.Email.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var blockedEmails = contacts
            .Where(contact => contact.UnsubscribedAtUtc is not null || contact.SuppressedAtUtc is not null)
            .Select(contact => contact.Email)
            .ToHashSet(StringComparer.Ordinal);
        var recipients = users
            .Where(user => !user.IsDeactivated && user.MarketingEmailConsent == true &&
                !blockedEmails.Contains(user.Email.Trim().ToLowerInvariant()))
            .Select(user => new MarketingRecipient(user.Id, user.Email, user.Language ?? "en"))
            .GroupBy(recipient => recipient.Email.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        recipients.AddRange(contacts
            .Where(contact => contact.Source == "waitlist" && !userEmails.Contains(contact.Email) &&
                contact.UnsubscribedAtUtc is null && contact.SuppressedAtUtc is null)
            .Select(contact => new MarketingRecipient(contact.Id, contact.Email, contact.Language)));

        LogBroadcastQueued(logger, recipients.Count, request.SubjectEn);
        var broadcastId = Guid.NewGuid();
        var nowAtUtc = DateTime.UtcNow;
        var deliveries = recipients.Select((recipient, index) => MarketingDelivery.Create(
            broadcastId, recipient.UserId, recipient.Email, recipient.Language,
            request.SubjectEn, request.SubjectPt, request.BodyHtmlEn, request.BodyHtmlPt,
            nowAtUtc.AddMilliseconds((long)index * Math.Max(0, _settings.SendDelayMilliseconds)))).ToList();
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
                    delivery.RecordDispatch(jobId, DateTime.UtcNow);
                else
                    delivery.DeferRecovery(DateTime.UtcNow);
            }
            await unitOfWork.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success(new MarketingBroadcastResult(recipients.Count, WasTest: false));
    }

    private static RenderedContent RenderFor(SendMarketingBroadcastCommand request, string language) =>
        LocaleHelper.IsPortuguese(language)
            ? new RenderedContent(request.SubjectPt, request.BodyHtmlPt)
            : new RenderedContent(request.SubjectEn, request.BodyHtmlEn);

    private string BuildUnsubscribeUrl(string token, string language) =>
        $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe" +
        $"?token={Uri.EscapeDataString(token)}&lang={(LocaleHelper.IsPortuguese(language) ? "pt" : "en")}";

    private sealed record MarketingRecipient(Guid UserId, string Email, string Language);

    private sealed record RenderedContent(string Subject, string BodyHtml);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing broadcast preview sent; subject={Subject}")]
    private static partial void LogPreviewSent(ILogger logger, string subject);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Marketing broadcast queued: {RecipientCount} recipients, subject={Subject}")]
    private static partial void LogBroadcastQueued(ILogger logger, int recipientCount, string subject);

}
