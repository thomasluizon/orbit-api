using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public class ResendEmailService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options) : IEmailService
{
    private readonly ResendSettings _settings = options.Value;

    public async Task SendWelcomeEmailAsync(string toEmail, string userName, CancellationToken cancellationToken = default)
    {
        var html = BuildWelcomeEmailHtml(userName);
        await SendEmailAsync(toEmail, "Welcome to Orbit!", html, cancellationToken);
    }

    private async Task SendEmailAsync(string to, string subject, string html, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Resend");

        var payload = new
        {
            from = _settings.FromEmail,
            to = new[] { to },
            subject,
            html
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        await client.PostAsync("/emails", content, cancellationToken);
    }

    private static string BuildWelcomeEmailHtml(string userName)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Welcome to Orbit</title>
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
                            <!-- Welcome heading -->
                            <h1 style=""margin: 0 0 8px 0; font-size: 24px; font-weight: 700; color: #ffffff;"">Welcome aboard, {System.Net.WebUtility.HtmlEncode(userName)}!</h1>
                            <p style=""margin: 0 0 24px 0; font-size: 15px; color: #a3a3a3; line-height: 1.6;"">
                                We're excited to have you on Orbit. You're now ready to build better habits, track your progress, and stay on top of your goals.
                            </p>
                            <!-- Divider -->
                            <div style=""height: 1px; background-color: #262626; margin: 24px 0;""></div>
                            <!-- Features -->
                            <p style=""margin: 0 0 16px 0; font-size: 14px; font-weight: 600; color: #ffffff;"">Here's what you can do:</p>
                            <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" width=""100%"">
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> Create daily, weekly, or custom habits
                                    </td>
                                </tr>
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> Track streaks and view your progress
                                    </td>
                                </tr>
                                <tr>
                                    <td style=""padding: 8px 0; font-size: 14px; color: #a3a3a3;"">
                                        <span style=""color: #8b5cf6; font-weight: 700; margin-right: 8px;"">&#10022;</span> Get AI-powered insights on your routines
                                    </td>
                                </tr>
                            </table>
                            <!-- CTA Button -->
                            <div style=""text-align: center; padding-top: 32px;"">
                                <a href=""https://app.useorbit.org"" style=""display: inline-block; background-color: #8b5cf6; color: #ffffff; font-size: 15px; font-weight: 700; text-decoration: none; padding: 14px 40px; border-radius: 100px; box-shadow: 0 10px 15px -3px rgba(139, 92, 246, 0.3);"">Get Started</a>
                            </div>
                        </td>
                    </tr>
                    <!-- Footer -->
                    <tr>
                        <td style=""text-align: center; padding-top: 32px;"">
                            <p style=""margin: 0; font-size: 13px; color: #525252;"">
                                The Orbit Team
                            </p>
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
