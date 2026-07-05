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
                LogContactAdded(logger, email);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogContactAddFailed(logger, email, response.StatusCode, body);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Waitlist contact added for {Email}")]
    private static partial void LogContactAdded(ILogger logger, string email);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Waitlist contact add failed for {Email} status={Status} body={Body}")]
    private static partial void LogContactAddFailed(ILogger logger, string email, System.Net.HttpStatusCode status, string body);
}
