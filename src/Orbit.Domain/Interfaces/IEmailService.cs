namespace Orbit.Domain.Interfaces;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default);
    Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default);
    Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default);
    Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default);
    Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one marketing broadcast email: wraps <paramref name="bodyHtml"/> in the branded shell
    /// with a localized legal/unsubscribe footer and one-click List-Unsubscribe headers pointing at
    /// <paramref name="unsubscribeUrl"/>. Retries with exponential backoff on Resend 429/5xx so a
    /// mass send paces itself rather than tripping the provider rate limit.
    /// </summary>
    Task SendMarketingEmailAsync(string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl, CancellationToken cancellationToken = default);
}
