using FluentValidation;
using Orbit.Application.Marketing.Validators;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Jobs;

public sealed class SendMarketingEmailJob(IEmailService emailService,
    IGenericRepository<MarketingDelivery> deliveries, IUnitOfWork unitOfWork,
    IMarketingUnsubscribeTokenService tokens, IOptions<MarketingSettings> settings)
{
    public Task ExecuteAsync(string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl) =>
        emailService.SendMarketingEmailAsync(toEmail, subject, bodyHtml, language, unsubscribeUrl);

    public async Task ExecuteRecipientAsync(Guid broadcastId, Guid recipientId, string email, string language)
    {
        await new MarketingRecipientDeliveryValidator().ValidateAndThrowAsync(new(broadcastId, recipientId, email, language));
        await unitOfWork.ExecuteOnceInTransactionAsync(async cancellationToken =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync("marketing-delivery", cancellationToken);
            var delivery = await deliveries.FindOneTrackedAsync(
                row => row.BroadcastId == broadcastId && row.RecipientId == recipientId && row.Email == email && row.Language == language,
                cancellationToken: cancellationToken);
            if (delivery is null)
                return;
            await deliveries.ReloadAsync(delivery, cancellationToken);
            if (delivery.SentAtUtc is not null)
                return;
            var nowAtUtc = DateTime.UtcNow;
            if (delivery.NotBeforeAtUtc > nowAtUtc)
                await Task.Delay(delivery.NotBeforeAtUtc - nowAtUtc, cancellationToken);
            var portuguese = LocaleHelper.IsPortuguese(delivery.Language);
            var url = $"{settings.Value.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe" +
                $"?token={Uri.EscapeDataString(tokens.CreateToken(delivery.RecipientId))}&lang={(portuguese ? "pt" : "en")}";
            await emailService.SendMarketingEmailAsync(delivery.Email,
                portuguese ? delivery.SubjectPt : delivery.SubjectEn,
                portuguese ? delivery.BodyHtmlPt : delivery.BodyHtmlEn,
                delivery.Language, url, cancellationToken);
            delivery.MarkSent(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (settings.Value.SendDelayMilliseconds > 0)
                await Task.Delay(settings.Value.SendDelayMilliseconds, cancellationToken);
        });
    }
}
