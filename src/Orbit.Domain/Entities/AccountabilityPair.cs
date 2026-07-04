using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class AccountabilityPair : Entity
{
    public Guid RequesterId { get; private set; }
    public Guid AddresseeId { get; private set; }
    public AccountabilityPairStatus Status { get; private set; }
    public AccountabilityCadence Cadence { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }

    private AccountabilityPair() { }

    public static Result<AccountabilityPair> Create(Guid requesterId, Guid addresseeId, AccountabilityCadence cadence)
    {
        if (requesterId == addresseeId)
            return Result.Failure<AccountabilityPair>(DomainErrors.CannotPairSelf);

        return Result.Success(new AccountabilityPair
        {
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = AccountabilityPairStatus.Pending,
            Cadence = cadence,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result Accept()
    {
        if (Status != AccountabilityPairStatus.Pending)
            return Result.Failure(DomainErrors.PairNotPending);

        Status = AccountabilityPairStatus.Accepted;
        AcceptedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public void End()
    {
        Status = AccountabilityPairStatus.Ended;
        EndedAtUtc = DateTime.UtcNow;
    }
}
