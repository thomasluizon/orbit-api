using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed partial class ResendAudienceService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> options,
    ILogger<ResendAudienceService> logger) : IMarketingAudienceService
{
    private readonly ResendSettings _settings = options.Value;

    public async Task AddContactAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.AudienceId))
            throw new InvalidOperationException("Resend:AudienceId is not configured.");

        var client = httpClientFactory.CreateClient("Resend");

        var content = new StringContent(
            JsonSerializer.Serialize(new { email, unsubscribed = false }),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(
            $"/audiences/{_settings.AudienceId}/contacts",
            content,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            if (logger.IsEnabled(LogLevel.Information))
                LogContactAdded(logger, email);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogContactAddFailed(logger, email, response.StatusCode, body);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Waitlist contact added to audience for {Email}")]
    private static partial void LogContactAdded(ILogger logger, string email);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Waitlist contact add failed for {Email} status={Status} body={Body}")]
    private static partial void LogContactAddFailed(ILogger logger, string email, System.Net.HttpStatusCode status, string body);
}
