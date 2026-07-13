using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Services;

public partial class ResendEmailService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private const int MaxMarketingRetries = 4;

    private readonly ResendSettings _settings = options.Value;
    private readonly string _frontendBaseUrl = frontendSettings.Value.BaseUrl;

    private string LogoUrl => $"{_frontendBaseUrl}/logo-no-bg.png";

    public async Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var htmlCopy = EmailCopy.Welcome(isPtBr, WebUtility.HtmlEncode(userName));
        var textCopy = EmailCopy.Welcome(isPtBr, userName);

        var layout = new EmailLayout(LangCode(isPtBr), htmlCopy.Preheader, htmlCopy.Footer, LogoUrl, GradientHeader: true);
        var html = EmailTemplateRenderer.RenderHtml("Welcome", layout, WelcomeTokens(htmlCopy));
        var text = EmailTemplateRenderer.RenderText("Welcome", WelcomeTokens(textCopy));

        await SendEmailAsync(toEmail, htmlCopy.Subject, html, text, cancellationToken);
    }

    public async Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.VerificationCode(isPtBr);
        var signInUrl = $"{_frontendBaseUrl}/login?email={WebUtility.UrlEncode(toEmail)}&code={code}";

        var tokens = new Dictionary<string, string>
        {
            ["heading"] = copy.Heading,
            ["intro"] = copy.Intro,
            ["code"] = code,
            ["cta"] = copy.Cta,
            ["signInUrl"] = signInUrl,
            ["warning"] = copy.Warning,
            ["footer"] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl, GradientHeader: false);
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("VerificationCode", tokens);

        await SendEmailAsync(toEmail, copy.Subject, html, text, cancellationToken);
    }

    public async Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.AccountDeletion(isPtBr);

        var tokens = new Dictionary<string, string>
        {
            ["heading"] = copy.Heading,
            ["intro"] = copy.Intro,
            ["codeLabel"] = copy.CodeLabel,
            ["code"] = code,
            ["warning"] = copy.Warning,
            ["footer"] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl, GradientHeader: false);
        var html = EmailTemplateRenderer.RenderHtml("AccountDeletion", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("AccountDeletion", tokens);

        await SendEmailAsync(toEmail, copy.Subject, html, text, cancellationToken);
    }

    public async Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.WaitlistConfirmation(isPtBr);

        var tokens = new Dictionary<string, string>
        {
            ["heading"] = copy.Heading,
            ["intro"] = copy.Intro,
            ["cta"] = copy.Cta,
            ["confirmUrl"] = confirmUrl,
            ["warning"] = copy.Warning,
            ["footer"] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl, GradientHeader: true);
        var html = EmailTemplateRenderer.RenderHtml("WaitlistConfirmation", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("WaitlistConfirmation", tokens);

        await SendEmailAsync(toEmail, copy.Subject, html, text, cancellationToken);
    }

    public async Task SendMarketingEmailAsync(
        string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl, CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var footer = MarketingFooterHtml(isPtBr, unsubscribeUrl);
        var layout = new EmailLayout(LangCode(isPtBr), Preheader: "", footer, LogoUrl, GradientHeader: true);
        var readableBody =
            "<div style=\"font-family: Rubik, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; " +
            $"font-size: 16px; line-height: 1.6; color: #E2E8F0;\">{bodyHtml}</div>";
        var html = EmailTemplateRenderer.RenderLayout(layout, readableBody);

        var payload = new
        {
            from = _settings.MarketingFromEmail,
            to = new[] { toEmail },
            subject,
            html,
            headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            },
        };

        await SendMarketingWithBackoffAsync(toEmail, subject, JsonSerializer.Serialize(payload), cancellationToken);
    }

    private static string MarketingFooterHtml(bool isPtBr, string unsubscribeUrl)
    {
        const string legalIdentity =
            "TL SOFTWARE ENGINEERING LTDA · CNPJ 58.429.979/0001-06 · Av. Nova Independência, 651, Brooklin Paulista, São Paulo/SP · CEP 04570-001";

        var (reason, unsubscribeLabel) = isPtBr
            ? ("Você está recebendo este e-mail porque optou por receber novidades do Orbit.", "Cancelar inscrição")
            : ("You're receiving this because you opted in to product updates from Orbit.", "Unsubscribe");

        var encodedUrl = WebUtility.HtmlEncode(unsubscribeUrl);
        return $"{reason}<br>{legalIdentity}<br>" +
            $"<a href=\"{encodedUrl}\" style=\"color: #90A1B9; text-decoration: underline;\">{unsubscribeLabel}</a>";
    }

    private async Task SendMarketingWithBackoffAsync(string to, string subject, string serializedPayload, CancellationToken cancellationToken)
    {
        if (IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogSkippingTestEmail(logger, subject);
            return;
        }

        var client = httpClientFactory.CreateClient("Resend");

        for (var attempt = 0; attempt <= MaxMarketingRetries; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");
                response = await client.PostAsync("/emails", content, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogEmailSendException(logger, ex);
                return;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        LogEmailSent(logger, subject);
                    return;
                }

                var isRetriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!isRetriable || attempt == MaxMarketingRetries)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                        LogEmailFailed(logger, subject, response.StatusCode);
                    return;
                }
            }

            var backoff = TimeSpan.FromMilliseconds(_settings.MarketingRetryBaseDelayMs * Math.Pow(2, attempt));
            if (logger.IsEnabled(LogLevel.Warning))
                LogMarketingRetry(logger, attempt + 1, backoff.TotalMilliseconds);
            await Task.Delay(backoff, cancellationToken);
        }
    }

    public async Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default)
    {
        const string supportFooter = "Reply directly to respond to the user.";

        var htmlTokens = new Dictionary<string, string>
        {
            ["fromName"] = WebUtility.HtmlEncode(fromName),
            ["fromEmail"] = WebUtility.HtmlEncode(fromEmail),
            ["subject"] = WebUtility.HtmlEncode(subject),
            ["message"] = WebUtility.HtmlEncode(message).Replace("\n", "<br>"),
        };

        var textTokens = new Dictionary<string, string>
        {
            ["fromName"] = fromName,
            ["fromEmail"] = fromEmail,
            ["subject"] = subject,
            ["message"] = message,
        };

        var layout = new EmailLayout("en", Preheader: "", Footer: supportFooter, LogoUrl, GradientHeader: false);
        var html = EmailTemplateRenderer.RenderHtml("Support", layout, htmlTokens);
        var text = EmailTemplateRenderer.RenderText("Support", textTokens);

        await SendEmailAsync(_settings.SupportEmail, $"[Orbit Support] {subject}", html, text, cancellationToken, replyTo: fromEmail);
    }

    private static string LangCode(bool isPtBr) => isPtBr ? "pt-BR" : "en";

    private Dictionary<string, string> WelcomeTokens(EmailCopy.WelcomeCopy copy) => new()
    {
        ["heading"] = copy.Heading,
        ["intro"] = copy.Intro,
        ["featuresTitle"] = copy.FeaturesTitle,
        ["feature1"] = copy.Feature1,
        ["feature2"] = copy.Feature2,
        ["feature3"] = copy.Feature3,
        ["cta"] = copy.Cta,
        ["ctaUrl"] = _frontendBaseUrl,
        ["footer"] = copy.Footer,
    };

    private async Task SendEmailAsync(string to, string subject, string html, string text, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogSkippingTestEmail(logger, subject);
            return;
        }

        var client = httpClientFactory.CreateClient("Resend");

        object payload = replyTo != null
            ? new { from = _settings.FromEmail, to = new[] { to }, subject, html, text, reply_to = replyTo }
            : new { from = _settings.FromEmail, to = new[] { to }, subject, html, text };
        var serializedPayload = JsonSerializer.Serialize(payload);

        try
        {
            using var response = await HttpRetryPolicy.SendWithRetryAsync(
                () => client.PostAsync(
                    "/emails",
                    new StringContent(serializedPayload, Encoding.UTF8, "application/json"),
                    cancellationToken),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    LogEmailSent(logger, subject);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Error))
                    LogEmailFailed(logger, subject, response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEmailSendException(logger, ex);
        }
    }

    private static bool IsTestAccount(string to)
    {
        var testAccountsEnv = Environment.GetEnvironmentVariable("TEST_ACCOUNTS");
        if (string.IsNullOrEmpty(testAccountsEnv))
            return false;

        var toNormalized = to.Trim();
        foreach (var pair in testAccountsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length >= 1 && string.Equals(parts[0].Trim(), toNormalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Skipping email to test account; subject={Subject}")]
    private static partial void LogSkippingTestEmail(ILogger logger, string subject);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Email sent; subject={Subject}")]
    private static partial void LogEmailSent(ILogger logger, string subject);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Email failed; subject={Subject} status={Status}")]
    private static partial void LogEmailFailed(ILogger logger, string subject, System.Net.HttpStatusCode status);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Email send exception")]
    private static partial void LogEmailSendException(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Marketing email rate-limited; retry {Attempt} after {BackoffMs}ms")]
    private static partial void LogMarketingRetry(ILogger logger, int attempt, double backoffMs);

}
