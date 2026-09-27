namespace Orbit.Domain.Interfaces;

/// <summary>
/// Stores a confirmed waitlist subscriber for marketing broadcasts.
/// </summary>
public interface IMarketingContactsService
{
    Task AddContactAsync(string email, string language, CancellationToken cancellationToken = default);
}
