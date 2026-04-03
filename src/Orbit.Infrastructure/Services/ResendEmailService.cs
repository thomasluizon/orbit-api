using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public partial class ResendEmailService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly ResendSettings _settings = options.Value;
    private readonly string _frontendBaseUrl = frontendSettings.Value.BaseUrl;

    public async Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Boas-vindas ao Orbit!" : "Welcome to Orbit!";
        var html = BuildWelcomeEmailHtml(userName, isPtBr);
        await SendEmailAsync(toEmail, subject, html, cancellationToken);
    }

    public async Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Seu código de verificação do Orbit" : "Your Orbit verification code";
        var html = BuildVerificationCodeEmailHtml(code, toEmail, isPtBr);
        await SendEmailAsync(toEmail, subject, html, cancellationToken);
    }

    public async Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Confirme a exclusão da sua conta Orbit" : "Confirm your Orbit account deletion";
        var html = BuildAccountDeletionEmailHtml(code, isPtBr);
        await SendEmailAsync(toEmail, subject, html, cancellationToken);
    }

    public async Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default)
    {
        var encodedName = System.Net.WebUtility.HtmlEncode(fromName);
        var encodedEmail = System.Net.WebUtility.HtmlEncode(fromEmail);
        var encodedSubject = System.Net.WebUtility.HtmlEncode(subject);
        var encodedMessage = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br>");

        var html = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Orbit Support</title>
</head>
<body style=""margin: 0; padding: 0; background-color: #0a0a0a; font-family: 'Manrope', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
    <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""background-color: #0a0a0a;"">
        <tr>
            <td style=""padding: 40px 20px;"">
                <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""max-width: 520px; margin: 0 auto;"">
                    <tr>
                        <td style=""text-align: center; padding-bottom: 32px;"">
                            <span style=""font-size: 28px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">Orbit</span>
                        </td>
                    </tr>
                    <tr>
                        <td style=""background-color: #141414; border-radius: 24px; border: 1px solid #262626; padding: 40px 32px;"">
                            <h1 style=""margin: 0 0 24px 0; font-size: 22px; font-weight: 700; color: #8b5cf6;"">Support Request</h1>
                            <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"">
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 13px; color: #525252; font-weight: 600;"">FROM</td>
                                </tr>
                                <tr>
                                    <td style=""padding: 0 0 16px 0; font-size: 15px; color: #ffffff;"">{encodedName} &lt;{encodedEmail}&gt;</td>
                                </tr>
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 13px; color: #525252; font-weight: 600;"">SUBJECT</td>
                                </tr>
                                <tr>
                                    <td style=""padding: 0 0 16px 0; font-size: 15px; color: #ffffff;"">{encodedSubject}</td>
                                </tr>
                                <tr>
                                    <td style=""height: 1px; background-color: #262626;""></td>
                                </tr>
                                <tr>
                                    <td style=""padding: 16px 0 0 0; font-size: 15px; color: #a3a3a3; line-height: 1.6;"">{encodedMessage}</td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    <tr>
                        <td style=""text-align: center; padding-top: 32px;"">
                            <p style=""margin: 0; font-size: 13px; color: #525252;"">Reply directly to respond to the user.</p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";

        await SendEmailAsync(
            _settings.SupportEmail,
            $"[Orbit Support] {subject}",
            html,
            cancellationToken,
            replyTo: fromEmail);
    }

    private async Task SendEmailAsync(string to, string subject, string html, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Information))
                LogSkippingTestEmail(logger, to, subject);
            return;
        }

        var client = httpClientFactory.CreateClient("Resend");

        object payload = replyTo != null
            ? new { from = _settings.FromEmail, to = new[] { to }, subject, html, reply_to = replyTo }
            : new { from = _settings.FromEmail, to = new[] { to }, subject, html };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        try
        {
            var response = await client.PostAsync("/emails", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    LogEmailSent(logger, to, subject);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Error))
                    LogEmailFailed(logger, to, response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            LogEmailSendException(logger, ex, to);
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

    private string BuildVerificationCodeEmailHtml(string code, string email, bool isPtBr)
    {
        var heading = isPtBr ? "Seu código de verificação" : "Your verification code";
        var intro = isPtBr
            ? "Use o código abaixo para entrar no Orbit. Ele expira em 5 minutos."
            : "Use the code below to sign in to Orbit. It expires in 5 minutes.";
        var warning = isPtBr
            ? "Se você não solicitou este código, pode ignorar este e-mail."
            : "If you didn't request this code, you can safely ignore this email.";
        var footer = isPtBr ? "Equipe Orbit" : "The Orbit Team";
        var signInButtonText = isPtBr ? "Entrar no Orbit" : "Sign in to Orbit";

        var encodedEmail = System.Net.WebUtility.UrlEncode(email);
        var signInUrl = $"{_frontendBaseUrl}/login?email={encodedEmail}&code={code}";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Orbit</title>
</head>
<body style=""margin: 0; padding: 0; background-color: #0a0a0a; font-family: 'Manrope', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
    <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""background-color: #0a0a0a;"">
        <tr>
            <td style=""padding: 40px 20px;"">
                <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""max-width: 520px; margin: 0 auto;"">
                    <tr>
                        <td style=""text-align: center; padding-bottom: 32px;"">
                            <span style=""font-size: 28px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">Orbit</span>
                        </td>
                    </tr>
                    <tr>
                        <td style=""background-color: #141414; border-radius: 24px; border: 1px solid #262626; padding: 40px 32px;"">
                            <h1 style=""margin: 0 0 8px 0; font-size: 24px; font-weight: 700; color: #ffffff; text-align: center;"">{heading}</h1>
                            <p style=""margin: 0 0 32px 0; font-size: 15px; color: #a3a3a3; line-height: 1.6; text-align: center;"">{intro}</p>
                            <div style=""text-align: center; padding: 24px 0; background-color: #0a0a0a; border-radius: 16px; margin: 0 0 24px 0;"">
                                <span style=""font-size: 32px; font-weight: 800; color: #8b5cf6; letter-spacing: 8px; font-family: 'Courier New', Courier, monospace; white-space: nowrap;"">{code}</span>
                            </div>
                            <div style=""text-align: center; padding-top: 8px; margin: 0 0 24px 0;"">
                                <a href=""{signInUrl}"" style=""display: inline-block; background-color: #8b5cf6; color: #ffffff; font-size: 15px; font-weight: 700; text-decoration: none; padding: 14px 40px; border-radius: 100px; box-shadow: 0 10px 15px -3px rgba(139, 92, 246, 0.3);"">{signInButtonText}</a>
                            </div>
                            <p style=""margin: 0; font-size: 13px; color: #525252; text-align: center;"">{warning}</p>
                        </td>
                    </tr>
                    <tr>
                        <td style=""text-align: center; padding-top: 32px;"">
                            <p style=""margin: 0; font-size: 13px; color: #525252;"">{footer}</p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    private static string BuildAccountDeletionEmailHtml(string code, bool isPtBr)
    {
        var heading = isPtBr ? "Exclusão de conta" : "Account deletion";
        var intro = isPtBr
            ? "Você solicitou a exclusão da sua conta Orbit. Essa ação é irreversível. Todos os seus dados serão permanentemente excluídos, incluindo hábitos, histórico, conversas e configurações."
            : "You requested to delete your Orbit account. This action is irreversible. All your data will be permanently deleted, including habits, history, conversations, and settings.";
        var codeLabel = isPtBr ? "Use o código abaixo para confirmar:" : "Use the code below to confirm:";
        var warning = isPtBr
            ? "Se você não solicitou isso, ignore este e-mail. Sua conta permanecerá segura."
            : "If you didn't request this, ignore this email. Your account will remain safe.";
        var footer = isPtBr ? "Equipe Orbit" : "The Orbit Team";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Orbit</title>
</head>
<body style=""margin: 0; padding: 0; background-color: #0a0a0a; font-family: 'Manrope', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
    <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""background-color: #0a0a0a;"">
        <tr>
            <td style=""padding: 40px 20px;"">
                <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""max-width: 520px; margin: 0 auto;"">
                    <tr>
                        <td style=""text-align: center; padding-bottom: 32px;"">
                            <span style=""font-size: 28px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">Orbit</span>
                        </td>
                    </tr>
                    <tr>
                        <td style=""background-color: #141414; border-radius: 24px; border: 1px solid #dc2626; padding: 40px 32px;"">
                            <h1 style=""margin: 0 0 8px 0; font-size: 24px; font-weight: 700; color: #dc2626; text-align: center;"">{heading}</h1>
                            <p style=""margin: 0 0 16px 0; font-size: 15px; color: #a3a3a3; line-height: 1.6; text-align: center;"">{intro}</p>
                            <p style=""margin: 0 0 16px 0; font-size: 15px; color: #ffffff; text-align: center; font-weight: 600;"">{codeLabel}</p>
                            <div style=""text-align: center; padding: 24px 0; background-color: #0a0a0a; border-radius: 16px; margin: 0 0 24px 0;"">
                                <span style=""font-size: 32px; font-weight: 800; color: #dc2626; letter-spacing: 8px; font-family: 'Courier New', Courier, monospace; white-space: nowrap;"">{code}</span>
                            </div>
                            <p style=""margin: 0; font-size: 13px; color: #525252; text-align: center;"">{warning}</p>
                        </td>
                    </tr>
                    <tr>
                        <td style=""text-align: center; padding-top: 32px;"">
                            <p style=""margin: 0; font-size: 13px; color: #525252;"">{footer}</p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    private string BuildWelcomeEmailHtml(string userName, bool isPtBr)
    {
        var encodedName = System.Net.WebUtility.HtmlEncode(userName);
        var heading = isPtBr ? $"Boas-vindas, {encodedName}!" : $"Welcome aboard, {encodedName}!";
        var intro = isPtBr
            ? "Estamos animados em ter você no Orbit. Agora você pode construir hábitos melhores, acompanhar seu progresso e manter suas metas em dia."
            : "We're excited to have you on Orbit. You're now ready to build better habits, track your progress, and stay on top of your goals.";
        var featuresTitle = isPtBr ? "O que você pode fazer:" : "Here's what you can do:";
        var feature1 = isPtBr ? "Crie hábitos diários, semanais ou personalizados" : "Create daily, weekly, or custom habits";
        var feature2 = isPtBr ? "Acompanhe sequências e veja seu progresso" : "Track streaks and view your progress";
        var feature3 = isPtBr ? "Receba insights de IA sobre suas rotinas" : "Get AI-powered insights on your routines";
        var cta = isPtBr ? "Começar" : "Get Started";
        var footer = isPtBr ? "Equipe Orbit" : "The Orbit Team";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Orbit</title>
</head>
<body style=""margin: 0; padding: 0; background-color: #0a0a0a; font-family: 'Manrope', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
    <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""background-color: #0a0a0a;"">
        <tr>
            <td style=""padding: 40px 20px;"">
                <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"" style=""max-width: 520px; margin: 0 auto;"">
                    <!-- Logo -->
                    <tr>
                        <td style=""text-align: center; padding-bottom: 32px;"">
                            <span style=""font-size: 28px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">Orbit</span>
                        </td>
                    </tr>
                    <!-- Card -->
                    <tr>
                        <td style=""background-color: #141414; border-radius: 24px; border: 1px solid #262626; padding: 40px 32px;"">
                            <h1 style=""margin: 0 0 8px 0; font-size: 24px; font-weight: 700; color: #ffffff;"">{heading}</h1>
                            <p style=""margin: 0 0 24px 0; font-size: 15px; color: #a3a3a3; line-height: 1.6;"">{intro}</p>
                            <div style=""height: 1px; background-color: #262626; margin: 24px 0;""></div>
                            <p style=""margin: 0 0 16px 0; font-size: 14px; font-weight: 600; color: #ffffff;"">{featuresTitle}</p>
                            <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"">
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> {feature1}
                                    </td>
                                </tr>
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> {feature2}
                                    </td>
                                </tr>
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> {feature3}
                                    </td>
                                </tr>
                            </table>
                            <div style=""text-align: center; padding-top: 32px;"">
                                <a href=""{_frontendBaseUrl}"" style=""display: inline-block; background-color: #8b5cf6; color: #ffffff; font-size: 15px; font-weight: 700; text-decoration: none; padding: 14px 40px; border-radius: 100px; box-shadow: 0 10px 15px -3px rgba(139, 92, 246, 0.3);"">{cta}</a>
                            </div>
                        </td>
                    </tr>
                    <tr>
                        <td style=""text-align: center; padding-top: 32px;"">
                            <p style=""margin: 0; font-size: 13px; color: #525252;"">{footer}</p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Skipping email to test account {To} subject={Subject}")]
    private static partial void LogSkippingTestEmail(ILogger logger, string to, string subject);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Email sent to {To} subject={Subject}")]
    private static partial void LogEmailSent(ILogger logger, string to, string subject);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Email failed to {To} status={Status} body={Body}")]
    private static partial void LogEmailFailed(ILogger logger, string to, System.Net.HttpStatusCode status, string body);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Email send exception to {To}")]
    private static partial void LogEmailSendException(ILogger logger, Exception ex, string to);

}
