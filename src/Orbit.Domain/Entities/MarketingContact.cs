using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class MarketingContact : Entity
{
    public string Email { get; private set; } = "";
    public string Language { get; private set; } = "en";
    public string Source { get; private set; } = "waitlist";
    public DateTime ConfirmedAtUtc { get; private set; }
    public DateTime? UnsubscribedAtUtc { get; private set; }
    public DateTime? SuppressedAtUtc { get; private set; }

    private MarketingContact() { }

    public static MarketingContact ConfirmWaitlist(string email, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
            throw new ArgumentException("Invalid contact email.", nameof(email));
        if (language is not ("en" or "pt-BR"))
            throw new ArgumentException("Unsupported contact language.", nameof(language));

        return new MarketingContact
        {
            Email = normalized,
            Language = language,
            ConfirmedAtUtc = DateTime.UtcNow
        };
    }

    public static MarketingContact RecordUserOptOut(string email)
    {
        var contact = ConfirmWaitlist(email, "en");
        contact.Source = "user";
        contact.Unsubscribe();
        return contact;
    }

    public void Unsubscribe()
    {
        UnsubscribedAtUtc ??= DateTime.UtcNow;
    }

    public void Suppress()
    {
        SuppressedAtUtc ??= DateTime.UtcNow;
    }
}
