namespace Orbit.Domain.Interfaces;

/// <summary>
/// Adds a confirmed subscriber to the marketing contacts list used for the
/// iOS-launch waitlist. Distinct from <see cref="IEmailService"/>, which sends
/// transactional mail; this manages the subscriber list itself.
/// </summary>
public interface IMarketingContactsService
{
    Task AddContactAsync(string email, CancellationToken cancellationToken = default);
}
