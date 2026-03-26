using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public class ResendEmailService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly ResendSettings _settings = options.Value;

    public async Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Bem-vindo ao Orbit!" : "Welcome to Orbit!";
        var html = BuildWelcomeEmailHtml(userName, isPtBr);
        await SendEmailAsync(toEmail, subject, html, cancellationToken);
    }

    public async Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Seu código de verificação do Orbit" : "Your Orbit verification code";
        var html = BuildVerificationCodeEmailHtml(code, isPtBr);
        await SendEmailAsync(toEmail, subject, html, cancellationToken);
    }

    public async Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        var isPtBr = language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var subject = isPtBr ? "Confirme a exclusao da sua conta Orbit" : "Confirm your Orbit account deletion";
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
        // Suppress emails to test accounts
        var testAccountsEnv = Environment.GetEnvironmentVariable("TEST_ACCOUNTS");
        if (!string.IsNullOrEmpty(testAccountsEnv))
        {
            var toNormalized = to.Trim().ToLowerInvariant();
            foreach (var pair in testAccountsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':', 2);
                if (parts.Length >= 1 && parts[0].Trim().ToLowerInvariant() == toNormalized)
                {
                    logger.LogInformation("Skipping email to test account {To} subject={Subject}", to, subject);
                    return;
                }
            }
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
                logger.LogInformation("Email sent to {To} subject={Subject}", to, subject);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Email failed to {To} status={Status} body={Body}", to, response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email send exception to {To}", to);
        }
    }

    private static string BuildVerificationCodeEmailHtml(string code, bool isPtBr)
    {
        var heading = isPtBr ? "Seu código de verificação" : "Your verification code";
        var intro = isPtBr
            ? "Use o código abaixo para entrar no Orbit. Ele expira em 5 minutos."
            : "Use the code below to sign in to Orbit. It expires in 5 minutes.";
        var warning = isPtBr
            ? "Se você não solicitou este código, pode ignorar este e-mail."
            : "If you didn't request this code, you can safely ignore this email.";
        var footer = isPtBr ? "Equipe Orbit" : "The Orbit Team";

        // Format code with spaces between digits for readability
        var formattedCode = string.Join(" ", code.ToCharArray());

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
                                <span style=""font-size: 36px; font-weight: 800; color: #8b5cf6; letter-spacing: 12px; font-family: monospace;"">{formattedCode}</span>
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
        var heading = isPtBr ? "Exclusao de conta" : "Account deletion";
        var intro = isPtBr
            ? "Voce solicitou a exclusao da sua conta Orbit. Essa acao e irreversivel. Todos os seus dados serao permanentemente excluidos, incluindo habitos, historico, conversas e configuracoes."
            : "You requested to delete your Orbit account. This action is irreversible. All your data will be permanently deleted, including habits, history, conversations, and settings.";
        var codeLabel = isPtBr ? "Use o codigo abaixo para confirmar:" : "Use the code below to confirm:";
        var warning = isPtBr
            ? "Se voce nao solicitou isso, ignore este e-mail. Sua conta permanecera segura."
            : "If you didn't request this, ignore this email. Your account will remain safe.";
        var footer = isPtBr ? "Equipe Orbit" : "The Orbit Team";

        var formattedCode = string.Join(" ", code.ToCharArray());

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
                                <span style=""font-size: 36px; font-weight: 800; color: #dc2626; letter-spacing: 12px; font-family: monospace;"">{formattedCode}</span>
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

    private static string BuildWelcomeEmailHtml(string userName, bool isPtBr)
    {
        var encodedName = System.Net.WebUtility.HtmlEncode(userName);
        var heading = isPtBr ? $"Bem-vindo, {encodedName}!" : $"Welcome aboard, {encodedName}!";
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
                                <a href=""https://app.useorbit.org"" style=""display: inline-block; background-color: #8b5cf6; color: #ffffff; font-size: 15px; font-weight: 700; text-decoration: none; padding: 14px 40px; border-radius: 100px; box-shadow: 0 10px 15px -3px rgba(139, 92, 246, 0.3);"">{cta}</a>
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
}
