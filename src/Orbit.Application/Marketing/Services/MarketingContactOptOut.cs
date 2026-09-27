using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Services;

public static class MarketingContactOptOut
{
    public static async Task RecordAsync(
        string email,
        IGenericRepository<MarketingContact> contactRepository,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        await unitOfWork.AcquireAdvisoryLockAsync($"marketing-contact:{normalizedEmail}", cancellationToken);
        var contact = await contactRepository.FindOneTrackedAsync(
            candidate => candidate.Email == normalizedEmail,
            cancellationToken: cancellationToken);

        if (contact is null)
            await contactRepository.AddAsync(MarketingContact.RecordUserOptOut(normalizedEmail), cancellationToken);
        else
            contact.Unsubscribe();
    }
}
