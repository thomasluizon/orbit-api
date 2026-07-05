using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed partial class ResendContactsService(
    IHttpClientFactory httpClientFactory,
    IOptions<ResendSettings> settings,
    ILogger<ResendContactsService> logger) : IMarketingContactsService
{
    private readonly ResendSettings _settings = settings.Value;

    public async Task AddContactAsync(string email, CancellationToken cancellationToken = default)
    {
        var segmentId = NullIfBlank(_settings.WaitlistSegmentId);
        var outcome = await CreateContactAsync(email, properties: null, segmentId, cancellationToken);

        if (outcome == ContactWriteOutcome.AlreadyExists && segmentId is not null)
            await AddToSegmentAsync(email, segmentId, cancellationToken);

        if (outcome != ContactWriteOutcome.Failed && logger.IsEnabled(LogLevel.Information))
            LogContactAdded(logger, email);
    }

    public async Task UpsertProductContactAsync(string email, string? locale, string plan, CancellationToken cancellationToken = default)
    {
        var segmentId = NullIfBlank(_settings.ProductSegmentId);
        var properties = new Dictionary<string, string> { ["locale"] = locale ?? "", ["plan"] = plan };

        var outcome = await CreateContactAsync(email, properties, segmentId, cancellationToken);

        if (outcome != ContactWriteOutcome.AlreadyExists)
            return;

        await PatchContactAsync(email, unsubscribed: false, properties, cancellationToken);
        if (segmentId is not null)
            await AddToSegmentAsync(email, segmentId, cancellationToken);
    }

    public Task SetContactUnsubscribedAsync(string email, bool unsubscribed, CancellationToken cancellationToken = default) =>
        PatchContactAsync(email, unsubscribed, properties: null, cancellationToken);

    public async Task RemoveContactAsync(string email, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("Resend");
        var response = await client.DeleteAsync($"/contacts/{Uri.EscapeDataString(email)}", cancellationToken);
        await LogUnlessSuccessOrAsync(response, email, "delete", HttpStatusCode.NotFound, cancellationToken);
    }

    private async Task<ContactWriteOutcome> CreateContactAsync(
        string email,
        IReadOnlyDictionary<string, string>? properties,
        string? segmentId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Resend");
        var payload = new Dictionary<string, object> { ["email"] = email, ["unsubscribed"] = false };
        if (properties is not null) payload["properties"] = properties;
        if (segmentId is not null) payload["segments"] = new[] { segmentId };

        var response = await client.PostAsync("/contacts", JsonContent(payload), cancellationToken);
        if (response.IsSuccessStatusCode) return ContactWriteOutcome.Created;
        if (response.StatusCode == HttpStatusCode.Conflict) return ContactWriteOutcome.AlreadyExists;

        await LogFailureAsync(response, email, "create", cancellationToken);
        return ContactWriteOutcome.Failed;
    }

    private async Task PatchContactAsync(
        string email,
        bool unsubscribed,
        IReadOnlyDictionary<string, string>? properties,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Resend");
        var payload = new Dictionary<string, object> { ["unsubscribed"] = unsubscribed };
        if (properties is not null) payload["properties"] = properties;

        var response = await client.PatchAsync($"/contacts/{Uri.EscapeDataString(email)}", JsonContent(payload), cancellationToken);
        await LogUnlessSuccessOrAsync(response, email, "update", HttpStatusCode.NotFound, cancellationToken);
    }

    private async Task AddToSegmentAsync(string email, string segmentId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Resend");
        var response = await client.PostAsync(
            $"/contacts/{Uri.EscapeDataString(email)}/segments/{segmentId}", content: null, cancellationToken);
        await LogUnlessSuccessOrAsync(response, email, "segment-add", HttpStatusCode.Conflict, cancellationToken);
    }

    private async Task LogUnlessSuccessOrAsync(
        HttpResponseMessage response, string email, string operation, HttpStatusCode benign, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == benign)
            return;
        await LogFailureAsync(response, email, operation, cancellationToken);
    }

    private async Task LogFailureAsync(HttpResponseMessage response, string email, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogContactSyncFailed(logger, operation, email, response.StatusCode, body);
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private enum ContactWriteOutcome { Created, AlreadyExists, Failed }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Waitlist contact added for {Email}")]
    private static partial void LogContactAdded(ILogger logger, string email);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Resend contact {Operation} failed for {Email} status={Status} body={Body}")]
    private static partial void LogContactSyncFailed(ILogger logger, string operation, string email, HttpStatusCode status, string body);
}
