namespace Orbit.Domain.Interfaces;

/// <summary>
/// Manages the Resend marketing-contact list and its segment memberships. Distinct from
/// <see cref="IEmailService"/>, which sends transactional mail; this owns the subscriber list
/// itself. Every method is fire-and-forget: it treats an already-existing/absent contact as
/// success and never throws, so a Resend outage can never roll back the local decision that
/// triggered the sync.
/// </summary>
public interface IMarketingContactsService
{
    /// <summary>Adds a confirmed waitlist subscriber and places them in the waitlist segment.</summary>
    Task AddContactAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opt-in path: creates or updates the global contact with <c>unsubscribed:false</c> and the
    /// user's <paramref name="locale"/>/<paramref name="plan"/> custom properties, then ensures it
    /// belongs to the product segment so dashboard broadcasts can target it.
    /// </summary>
    Task UpsertProductContactAsync(string email, string? locale, string plan, CancellationToken cancellationToken = default);

    /// <summary>Opt-out path: sets the contact's global <c>unsubscribed</c> flag.</summary>
    Task SetContactUnsubscribedAsync(string email, bool unsubscribed, CancellationToken cancellationToken = default);

    /// <summary>Account-deletion path: deletes the global contact (LGPD erasure).</summary>
    Task RemoveContactAsync(string email, CancellationToken cancellationToken = default);
}
