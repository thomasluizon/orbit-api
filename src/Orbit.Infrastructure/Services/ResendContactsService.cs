using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed partial class ResendContactsService(
    IHttpClientFactory httpClientFactory,
    ILogger<ResendContactsService> logger) : IMarketingContactsService
{
    public async Task AddContactAsync(string email, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("Resend");

        var content = new StringContent(
            JsonSerializer.Serialize(new { email, unsubscribed = false }),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/contacts", content, cancellationToken);

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            if (logger.IsEnabled(LogLevel.Information))
                LogContactAdded(logger);
            return;
        }

        LogContactAddFailed(logger, response.StatusCode);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Waitlist contact added")]
    private static partial void LogContactAdded(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Waitlist contact add failed status={Status}")]
    private static partial void LogContactAddFailed(ILogger logger, System.Net.HttpStatusCode status);
}
