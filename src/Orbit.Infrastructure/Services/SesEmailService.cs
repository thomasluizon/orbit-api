using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Services;

public partial class SesEmailService(
    Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2 client,
    IOptions<SesSettings> options,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<SesEmailService> logger) : EmailServiceBase(frontendSettings.Value.BaseUrl, options.Value.SupportEmail)
{
    private const int MaxMarketingRetries = 4;
    private readonly SesSettings _settings = options.Value;

    protected override Task SendMarketingAsync(
        string to, string subject, string html, string unsubscribeUrl, CancellationToken cancellationToken) =>
        SendMarketingWithBackoffAsync(to, subject, html, unsubscribeUrl, cancellationToken);

    private async Task SendMarketingWithBackoffAsync(string to, string subject, string html, string unsubscribeUrl, CancellationToken cancellationToken)
    {
        if (EmailMessageComposer.IsTestAccount(to))
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
            catch (Exception ex) when (IsRetryableThrottle(ex, attempt))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                LogEmailSendException(logger, ex);
                return;
            }
        }
    }

    private static bool IsRetryableThrottle(Exception exception, int attempt) =>
        attempt < MaxMarketingRetries &&
        (exception is Amazon.SimpleEmailV2.Model.TooManyRequestsException ||
         exception is Amazon.Runtime.AmazonServiceException { StatusCode: HttpStatusCode.TooManyRequests });

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromMilliseconds(_settings.MarketingRetryBaseDelayMs * Math.Pow(2, attempt));
        if (logger.IsEnabled(LogLevel.Warning))
            LogMarketingRetry(logger, attempt + 1, backoff.TotalMilliseconds);
        await Task.Delay(backoff, cancellationToken);
    }

    protected override async Task SendTransactionalAsync(string to, string subject, string html, string? text, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (EmailMessageComposer.IsTestAccount(to))
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Skipping email to test account; subject={Subject}")]
    private static partial void LogSkippingTestEmail(ILogger logger, string subject);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Email sent; subject={Subject}")]
    private static partial void LogEmailSent(ILogger logger, string subject);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Email send exception")]
    private static partial void LogEmailSendException(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Marketing email rate-limited; retry {Attempt} after {BackoffMs}ms")]
    private static partial void LogMarketingRetry(ILogger logger, int attempt, double backoffMs);
}
