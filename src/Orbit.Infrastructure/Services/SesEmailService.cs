using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Services;

public partial class SesEmailService(
    Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2 client,
    IOptions<SesSettings> options,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<SesEmailService> logger) : IEmailService
{
    private const int MaxMarketingRetries = 4;
    private const string HeadingToken = "heading";
    private const string IntroToken = "intro";
    private const string FooterToken = "footer";

    private readonly SesSettings _settings = options.Value;
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
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["code"] = code,
            ["cta"] = copy.Cta,
            ["signInUrl"] = signInUrl,
            ["warning"] = copy.Warning,
            [FooterToken] = copy.Footer,
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
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["codeLabel"] = copy.CodeLabel,
            ["code"] = code,
            ["warning"] = copy.Warning,
            [FooterToken] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl, GradientHeader: false);
        var html = EmailTemplateRenderer.RenderHtml("AccountDeletion", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("AccountDeletion", tokens);

        await SendEmailAsync(toEmail, copy.Subject, html, text, cancellationToken);
    }

    public async Task SendApiKeyCreationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.ApiKeyCreation(isPtBr);

        var tokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["codeLabel"] = copy.CodeLabel,
            ["code"] = code,
            ["warning"] = copy.Warning,
            [FooterToken] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl, GradientHeader: false);
        var html = EmailTemplateRenderer.RenderHtml("ApiKeyCreation", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("ApiKeyCreation", tokens);

        await SendEmailAsync(toEmail, copy.Subject, html, text, cancellationToken);
    }

    public async Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.WaitlistConfirmation(isPtBr);

        var tokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["cta"] = copy.Cta,
            ["confirmUrl"] = confirmUrl,
            ["warning"] = copy.Warning,
            [FooterToken] = copy.Footer,
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

        await SendMarketingWithBackoffAsync(toEmail, subject, html, unsubscribeUrl, cancellationToken);
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

    private async Task SendMarketingWithBackoffAsync(string to, string subject, string html, string unsubscribeUrl, CancellationToken cancellationToken)
    {
        if (IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogSkippingTestEmail(logger, subject);
            return;
        }

        var request = CreateRequest(to, subject, html, null, _settings.MarketingFromEmail, _settings.MarketingConfigurationSet);
        request.Content.Simple.Headers =
        [
            new Amazon.SimpleEmailV2.Model.MessageHeader { Name = "List-Unsubscribe", Value = $"<{unsubscribeUrl}>" },
            new Amazon.SimpleEmailV2.Model.MessageHeader { Name = "List-Unsubscribe-Post", Value = "List-Unsubscribe=One-Click" },
        ];

        for (var attempt = 0; attempt <= MaxMarketingRetries; attempt++)
        {
            try
            {
                await client.SendEmailAsync(request, cancellationToken);
                if (logger.IsEnabled(LogLevel.Debug))
                    LogEmailSent(logger, subject);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Amazon.SimpleEmailV2.Model.TooManyRequestsException) when (attempt < MaxMarketingRetries)
            {
            }
            catch (Amazon.Runtime.AmazonServiceException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxMarketingRetries)
            {
            }
            catch (Exception ex)
            {
                LogEmailSendException(logger, ex);
                return;
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
        [HeadingToken] = copy.Heading,
        [IntroToken] = copy.Intro,
        ["featuresTitle"] = copy.FeaturesTitle,
        ["feature1"] = copy.Feature1,
        ["feature2"] = copy.Feature2,
        ["feature3"] = copy.Feature3,
        ["cta"] = copy.Cta,
        ["ctaUrl"] = _frontendBaseUrl,
        [FooterToken] = copy.Footer,
    };

    private async Task SendEmailAsync(string to, string subject, string html, string text, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogSkippingTestEmail(logger, subject);
            return;
        }

        var request = CreateRequest(to, subject, html, text, _settings.FromEmail, _settings.TransactionalConfigurationSet);
        if (replyTo is not null)
            request.ReplyToAddresses = [replyTo];

        try
        {
            await client.SendEmailAsync(request, cancellationToken);
            if (logger.IsEnabled(LogLevel.Debug))
                LogEmailSent(logger, subject);
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

    private static Amazon.SimpleEmailV2.Model.SendEmailRequest CreateRequest(
        string to, string subject, string html, string? text, string from, string configurationSet)
    {
        var body = new Amazon.SimpleEmailV2.Model.Body
        {
            Html = new Amazon.SimpleEmailV2.Model.Content { Data = html, Charset = "UTF-8" },
        };
        if (text is not null)
            body.Text = new Amazon.SimpleEmailV2.Model.Content { Data = text, Charset = "UTF-8" };

        return new Amazon.SimpleEmailV2.Model.SendEmailRequest
        {
            FromEmailAddress = from,
            Destination = new Amazon.SimpleEmailV2.Model.Destination { ToAddresses = [to] },
            ConfigurationSetName = configurationSet,
            Content = new Amazon.SimpleEmailV2.Model.EmailContent
            {
                Simple = new Amazon.SimpleEmailV2.Model.Message
                {
                    Subject = new Amazon.SimpleEmailV2.Model.Content { Data = subject, Charset = "UTF-8" },
                    Body = body,
                },
            },
        };
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Email send exception")]
    private static partial void LogEmailSendException(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Marketing email rate-limited; retry {Attempt} after {BackoffMs}ms")]
    private static partial void LogMarketingRetry(ILogger logger, int attempt, double backoffMs);

}
