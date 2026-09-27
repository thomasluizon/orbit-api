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

            var type = envelope.GetProperty("Type").GetString();
            if (type == "SubscriptionConfirmation")
            {
                var url = envelope.GetProperty("SubscribeURL").GetString();
                if (!verifier.IsSubscriptionUrl(url, envelope.GetProperty("TopicArn").GetString()!, envelope.GetProperty("Token").GetString()!))
                    return false;
                using var response = await httpClientFactory.CreateClient("SnsConfirmation")
                    .GetAsync(url, cancellationToken);
                return response.IsSuccessStatusCode;
            }

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

                await unitOfWork.ExecuteInTransactionAsync(async ct =>
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
                return true;
            }
        }
    }
    private HashSet<string>? GetSuppressedEmails(JsonElement eventData)
    {
        if (!eventData.TryGetProperty("eventType", out var eventType) || eventType.ValueKind != JsonValueKind.String)
            return null;
        var eventName = eventType.GetString();
        if (eventName is not ("Bounce" or "Complaint"))
            return [];
        var sectionName = eventName == "Bounce" ? "bounce" : "complaint";
        var recipientName = eventName == "Bounce" ? "bouncedRecipients" : "complainedRecipients";
        if (!eventData.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty(recipientName, out var recipients) || recipients.ValueKind != JsonValueKind.Array)
            return null;
        if (eventName == "Bounce" &&
            (!section.TryGetProperty("bounceType", out var bounceType) || bounceType.GetString() != "Permanent"))
        {
            logger.LogWarning("Transient SES bounce received");
            return [];
        }

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
