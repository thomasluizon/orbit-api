namespace Orbit.Domain.Interfaces;

/// <summary>
/// Adds a confirmed contact to the marketing audience used for the iOS-launch
/// waitlist. Distinct from <see cref="IEmailService"/>, which sends transactional
/// mail; this manages the subscriber list itself.
/// </summary>
public interface IMarketingAudienceService
{
    Task AddContactAsync(string email, CancellationToken cancellationToken = default);
}
