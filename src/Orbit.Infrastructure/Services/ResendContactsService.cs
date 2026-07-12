using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Common;

namespace Orbit.Infrastructure.Services;

public sealed partial class ResendContactsService(
    IHttpClientFactory httpClientFactory,
    ILogger<ResendContactsService> logger) : IMarketingContactsService
{
    public async Task AddContactAsync(string email, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("Resend");

        var serializedPayload = JsonSerializer.Serialize(new { email, unsubscribed = false });

        using var response = await HttpRetryPolicy.SendWithRetryAsync(
            () => client.PostAsync(
                "/contacts",
                new StringContent(serializedPayload, Encoding.UTF8, "application/json"),
                cancellationToken),
            cancellationToken);

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
