using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class DatabaseMarketingContactsService(
    IGenericRepository<MarketingContact> contacts,
    IUnitOfWork unitOfWork) : IMarketingContactsService
{
    public Task AddContactAsync(string email, string language, CancellationToken cancellationToken = default)
    {
        var contact = MarketingContact.ConfirmWaitlist(email, language);
        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"marketing-contact:{contact.Email}", ct);
            if (await contacts.AnyAsync(existing => existing.Email == contact.Email, ct))
                return;

            await contacts.AddAsync(contact, ct);
            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);
    }
}
