using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Jobs;

/// <summary>
/// Durable Hangfire job that dispatches a single marketing email. Enqueued by
/// <see cref="Commands.SendMarketingBroadcastCommandHandler"/> on the test-preview path so the request
/// returns immediately: the send runs out of band, survives a process restart, and is retried by Hangfire
/// on failure. Arguments are the ones Hangfire persists, so they stay primitive and serializable.
/// </summary>
public sealed class SendMarketingEmailJob(IEmailService emailService)
{
    public Task ExecuteAsync(string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl) =>
        emailService.SendMarketingEmailAsync(toEmail, subject, bodyHtml, language, unsubscribeUrl);
}
