using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Services;

public partial class ResendEmailService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<ResendEmailService> logger) : EmailServiceBase(frontendSettings.Value.BaseUrl, options.Value.SupportEmail)
{
    private const int MaxMarketingRetries = 4;
    private readonly ResendSettings _settings = options.Value;

    protected override Task SendMarketingAsync(
        string to, string subject, string html, string unsubscribeUrl, CancellationToken cancellationToken)
    {
        var payload = new
        {
            from = _settings.MarketingFromEmail,
            to = new[] { to },
            subject,
            html,
            headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            },
        };

        return SendMarketingWithBackoffAsync(to, subject, JsonSerializer.Serialize(payload), cancellationToken);
    }

    private async Task SendMarketingWithBackoffAsync(string to, string subject, string serializedPayload, CancellationToken cancellationToken)
    {
        if (EmailMessageComposer.IsTestAccount(to))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogSkippingTestEmail(logger, subject);
            return;
        }

        var client = httpClientFactory.CreateClient("Resend");

        for (var attempt = 0; attempt <= MaxMarketingRetries; attempt++)
        {
            var outcome = await TrySendMarketingAsync(client, subject, serializedPayload, attempt, cancellationToken);
            if (outcome == MarketingSendOutcome.Complete)
                return;

            var backoff = TimeSpan.FromMilliseconds(_settings.MarketingRetryBaseDelayMs * Math.Pow(2, attempt));
            if (logger.IsEnabled(LogLevel.Warning))
                LogMarketingRetry(logger, attempt + 1, backoff.TotalMilliseconds);
            await Task.Delay(backoff, cancellationToken);
        }
    }

    private enum MarketingSendOutcome { Complete, ShouldRetry }

    private async Task<MarketingSendOutcome> TrySendMarketingAsync(
        HttpClient client, string subject, string serializedPayload, int attempt, CancellationToken cancellationToken)
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
            return MarketingSendOutcome.Complete;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    LogEmailSent(logger, subject);
                return MarketingSendOutcome.Complete;
            }

            var isRetriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!isRetriable || attempt == MaxMarketingRetries)
            {
                if (logger.IsEnabled(LogLevel.Error))
                    LogEmailFailed(logger, subject, response.StatusCode);
                return MarketingSendOutcome.Complete;
            }
        }

        return MarketingSendOutcome.ShouldRetry;
    }

    protected override async Task SendTransactionalAsync(string to, string subject, string html, string? text, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (EmailMessageComposer.IsTestAccount(to))
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
