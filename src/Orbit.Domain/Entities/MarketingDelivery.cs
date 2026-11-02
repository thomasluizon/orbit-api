using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class MarketingDelivery : Entity
{
    public Guid BroadcastId { get; private set; }
    public Guid RecipientId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Language { get; private set; } = string.Empty;
    public string SubjectEn { get; private set; } = string.Empty;
    public string SubjectPt { get; private set; } = string.Empty;
    public string BodyHtmlEn { get; private set; } = string.Empty;
    public string BodyHtmlPt { get; private set; } = string.Empty;
    public DateTime NotBeforeAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }

    private MarketingDelivery() { }

    public static MarketingDelivery Create(Guid broadcastId, Guid recipientId, string email, string language,
        string subjectEn, string subjectPt, string bodyHtmlEn, string bodyHtmlPt, DateTime notBeforeAtUtc)
    {
        if (broadcastId == Guid.Empty || recipientId == Guid.Empty || notBeforeAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A delivery requires a broadcast, recipient and UTC schedule.");
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectEn);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectPt);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHtmlEn);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHtmlPt);
        if (email.Length > 320 || language.Length > 32)
            throw new ArgumentException("Delivery address or language exceeds its storage limit.");
        return new MarketingDelivery
        {
            BroadcastId = broadcastId,
            RecipientId = recipientId,
            Email = email,
            Language = language,
            SubjectEn = subjectEn,
            SubjectPt = subjectPt,
            BodyHtmlEn = bodyHtmlEn,
            BodyHtmlPt = bodyHtmlPt,
            NotBeforeAtUtc = notBeforeAtUtc
        };
    }

    public void MarkSent(DateTime sentAtUtc)
    {
        if (sentAtUtc.Kind != DateTimeKind.Utc || sentAtUtc < NotBeforeAtUtc)
            throw new ArgumentException("Delivery success must follow its UTC schedule.");
        SentAtUtc ??= sentAtUtc;
    }
}
