using System.Net;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Email;

internal sealed record EmailMessage(string Subject, string Html, string? Text);

internal sealed class EmailMessageComposer(string frontendBaseUrl)
{
    private const string HeadingToken = "heading";
    private const string IntroToken = "intro";
    private const string FooterToken = "footer";
    private const string WarningToken = "warning";

    private string LogoUrl => $"{frontendBaseUrl}/logo-no-bg.png";

    public EmailMessage ComposeWelcome(string userName, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var htmlCopy = EmailCopy.Welcome(isPtBr, WebUtility.HtmlEncode(userName));
        var textCopy = EmailCopy.Welcome(isPtBr, userName);

        var layout = new EmailLayout(LangCode(isPtBr), htmlCopy.Preheader, htmlCopy.Footer, LogoUrl);
        var html = EmailTemplateRenderer.RenderHtml("Welcome", layout, WelcomeTokens(htmlCopy));
        var text = EmailTemplateRenderer.RenderText("Welcome", WelcomeTokens(textCopy));

        return new EmailMessage(htmlCopy.Subject, html, text);
    }

    public EmailMessage ComposeVerificationCode(string toEmail, string code, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.VerificationCode(isPtBr);
        var signInUrl = $"{frontendBaseUrl}/login?email={WebUtility.UrlEncode(toEmail)}&code={code}";

        var tokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["code"] = code,
            ["cta"] = copy.Cta,
            ["signInUrl"] = signInUrl,
            [WarningToken] = copy.Warning,
            [FooterToken] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl);
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("VerificationCode", tokens);

        return new EmailMessage(copy.Subject, html, text);
    }

    public EmailMessage ComposeAccountDeletionCode(string code, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.AccountDeletion(isPtBr);
        return ComposeSecurityCode("AccountDeletion", copy.Subject, copy.Heading, copy.Intro,
            copy.CodeLabel, copy.Warning, copy.Footer, copy.Preheader, code, isPtBr);
    }

    public EmailMessage ComposeApiKeyCreationCode(string code, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.ApiKeyCreation(isPtBr);
        return ComposeSecurityCode("ApiKeyCreation", copy.Subject, copy.Heading, copy.Intro,
            copy.CodeLabel, copy.Warning, copy.Footer, copy.Preheader, code, isPtBr);
    }

    private EmailMessage ComposeSecurityCode(string template, string subject, string heading, string intro,
        string codeLabel, string warning, string footer, string preheader, string code, bool isPtBr)
    {
        var tokens = new Dictionary<string, string>
        {
            [HeadingToken] = heading,
            [IntroToken] = intro,
            ["codeLabel"] = codeLabel,
            ["code"] = code,
            [WarningToken] = warning,
            [FooterToken] = footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), preheader, footer, LogoUrl);
        var html = EmailTemplateRenderer.RenderHtml(template, layout, tokens);
        var text = EmailTemplateRenderer.RenderText(template, tokens);

        return new EmailMessage(subject, html, text);
    }

    public EmailMessage ComposeWaitlistConfirmation(string confirmUrl, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var copy = EmailCopy.WaitlistConfirmation(isPtBr);

        var tokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            [IntroToken] = copy.Intro,
            ["cta"] = copy.Cta,
            ["confirmUrl"] = confirmUrl,
            [WarningToken] = copy.Warning,
            [FooterToken] = copy.Footer,
        };

        var layout = new EmailLayout(LangCode(isPtBr), copy.Preheader, copy.Footer, LogoUrl);
        var html = EmailTemplateRenderer.RenderHtml("WaitlistConfirmation", layout, tokens);
        var text = EmailTemplateRenderer.RenderText("WaitlistConfirmation", tokens);

        return new EmailMessage(copy.Subject, html, text);
    }

    public EmailMessage ComposeMarketing(string subject, string bodyHtml, string language, string unsubscribeUrl)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var footer = MarketingFooterHtml(isPtBr, unsubscribeUrl);
        var layout = new EmailLayout(LangCode(isPtBr), Preheader: "", footer, LogoUrl);
        var readableBody =
            "<div class=\"orbit-fg-2\" style=\"font-family: 'Geist Sans', Geist, -apple-system, BlinkMacSystemFont, " +
            "'Segoe UI', Roboto, Helvetica, Arial, sans-serif; " +
            $"font-size: 16px; line-height: 1.6; color: #424247;\">{bodyHtml}</div>";
        var html = EmailTemplateRenderer.RenderLayout(layout, readableBody);

        return new EmailMessage(subject, html, null);
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
            $"<a class=\"orbit-fg-3\" href=\"{encodedUrl}\" style=\"color: #68686D; text-decoration: underline;\">{unsubscribeLabel}</a>";
    }

    public EmailMessage ComposeSupport(string fromName, string fromEmail, string subject, string message)
    {
        var copy = EmailCopy.Support();

        var htmlTokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            ["fromLabel"] = copy.FromLabel,
            ["subjectLabel"] = copy.SubjectLabel,
            ["fromName"] = WebUtility.HtmlEncode(fromName),
            ["fromEmail"] = WebUtility.HtmlEncode(fromEmail),
            ["subject"] = WebUtility.HtmlEncode(subject),
            ["message"] = WebUtility.HtmlEncode(message).Replace("\n", "<br>"),
        };

        var textTokens = new Dictionary<string, string>
        {
            [HeadingToken] = copy.Heading,
            ["fromLabel"] = copy.FromLabel,
            ["subjectLabel"] = copy.SubjectLabel,
            ["fromName"] = fromName,
            ["fromEmail"] = fromEmail,
            ["subject"] = subject,
            ["message"] = message,
            [FooterToken] = copy.Footer,
        };

        var layout = new EmailLayout("en", Preheader: "", Footer: copy.Footer, LogoUrl);
        var html = EmailTemplateRenderer.RenderHtml("Support", layout, htmlTokens);
        var text = EmailTemplateRenderer.RenderText("Support", textTokens);

        return new EmailMessage($"[Orbit Support] {subject}", html, text);
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
        ["ctaUrl"] = frontendBaseUrl,
        [FooterToken] = copy.Footer,
    };

    public static bool IsTestAccount(string to)
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
}
