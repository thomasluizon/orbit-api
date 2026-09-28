using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Email;

public abstract class EmailServiceBase(string frontendBaseUrl, string supportEmail) : IEmailService
{
    private readonly EmailMessageComposer _composer = new(frontendBaseUrl);

    public Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeWelcome(userName, language);
        return SendTransactionalAsync(toEmail, message.Subject, message.Html, message.Text, cancellationToken);
    }

    public Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeVerificationCode(toEmail, code, language);
        return SendTransactionalAsync(toEmail, message.Subject, message.Html, message.Text, cancellationToken);
    }

    public Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeAccountDeletionCode(code, language);
        return SendTransactionalAsync(toEmail, message.Subject, message.Html, message.Text, cancellationToken);
    }

    public Task SendApiKeyCreationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeApiKeyCreationCode(code, language);
        return SendTransactionalAsync(toEmail, message.Subject, message.Html, message.Text, cancellationToken);
    }

    public Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeWaitlistConfirmation(confirmUrl, language);
        return SendTransactionalAsync(toEmail, message.Subject, message.Html, message.Text, cancellationToken);
    }

    public Task SendMarketingEmailAsync(
        string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl, CancellationToken cancellationToken = default)
    {
        var message = _composer.ComposeMarketing(subject, bodyHtml, language, unsubscribeUrl);
        return SendMarketingAsync(toEmail, message.Subject, message.Html, unsubscribeUrl, cancellationToken);
    }

    public Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default)
    {
        var email = _composer.ComposeSupport(fromName, fromEmail, subject, message);
        return SendTransactionalAsync(supportEmail, email.Subject, email.Html, email.Text, cancellationToken, fromEmail);
    }

    protected abstract Task SendTransactionalAsync(
        string to, string subject, string html, string? text, CancellationToken cancellationToken, string? replyTo = null);

    protected abstract Task SendMarketingAsync(
        string to, string subject, string html, string unsubscribeUrl, CancellationToken cancellationToken);
}
