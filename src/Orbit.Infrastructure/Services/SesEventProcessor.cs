using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class SesEventProcessor(
    SnsMessageVerifier verifier,
    IHttpClientFactory httpClientFactory,
    IGenericRepository<MarketingContact> contacts,
    IUnitOfWork unitOfWork,
    ILogger<SesEventProcessor> logger) : ISesEventProcessor
{
    private const string BounceEvent = "Bounce";

    public async Task<bool> ProcessAsync(string payload, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return false; }
        using (document)
        {
            var envelope = document.RootElement;
            if (!await verifier.VerifyAsync(envelope, cancellationToken))
                return false;
            return envelope.GetProperty("Type").GetString() == "SubscriptionConfirmation"
                ? await ConfirmSubscriptionAsync(envelope, cancellationToken)
                : await ProcessNotificationAsync(envelope, cancellationToken);
        }
    }

    private async Task<bool> ConfirmSubscriptionAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        var url = envelope.GetProperty("SubscribeURL").GetString();
        if (!verifier.IsSubscriptionUrl(url, envelope.GetProperty("TopicArn").GetString()!, envelope.GetProperty("Token").GetString()!))
            return false;
        using var response = await httpClientFactory.CreateClient("SnsConfirmation")
            .GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> ProcessNotificationAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        if (!envelope.TryGetProperty("Message", out var message) || message.ValueKind != JsonValueKind.String)
            return false;
        JsonDocument eventDocument;
        try { eventDocument = JsonDocument.Parse(message.GetString()!); }
        catch (JsonException) { return false; }
        using (eventDocument)
        {
            var emails = GetSuppressedEmails(eventDocument.RootElement);
            if (emails is null)
                return false;
            if (emails.Count == 0)
                return true;
            await SuppressAsync(emails, cancellationToken);
            return true;
        }
    }

    private Task SuppressAsync(HashSet<string> emails, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var email in emails)
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"marketing-contact:{email}", ct);
                var contact = await contacts.FindOneTrackedAsync(
                    candidate => candidate.Email == email, cancellationToken: ct);
                if (contact is null)
                {
                    contact = MarketingContact.RecordUserOptOut(email);
                    await contacts.AddAsync(contact, ct);
                }
                else
                    contact.Unsubscribe();
                contact.Suppress();
            }
            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

    private HashSet<string>? GetSuppressedEmails(JsonElement eventData)
    {
        if (!eventData.TryGetProperty("eventType", out var eventType) || eventType.ValueKind != JsonValueKind.String)
            return null;
        var eventName = eventType.GetString();
        if (eventName is not (BounceEvent or "Complaint"))
            return [];
        var sectionName = eventName == BounceEvent ? "bounce" : "complaint";
        var recipientName = eventName == BounceEvent ? "bouncedRecipients" : "complainedRecipients";
        if (!eventData.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty(recipientName, out var recipients) || recipients.ValueKind != JsonValueKind.Array)
            return null;
        if (eventName == BounceEvent &&
            (!section.TryGetProperty("bounceType", out var bounceType) || bounceType.GetString() != "Permanent"))
        {
            logger.LogWarning("Transient SES bounce received");
            return [];
        }

        return ParseRecipientEmails(recipients);
    }

    private static HashSet<string>? ParseRecipientEmails(JsonElement recipients)
    {
        var emails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipient in recipients.EnumerateArray())
        {
            if (!recipient.TryGetProperty("emailAddress", out var email) || email.ValueKind != JsonValueKind.String)
                return null;
            var normalized = email.GetString()?.Trim().ToLowerInvariant();
            if (normalized is null || normalized.Length > 254 ||
                !System.Net.Mail.MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
                return null;
            emails.Add(normalized);
        }
        return emails;
    }

}
