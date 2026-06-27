using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Friendship : Entity
{
    public Guid RequesterId { get; private set; }
    public Guid AddresseeId { get; private set; }
    public FriendshipStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RespondedAtUtc { get; private set; }

    private Friendship() { }

    public static Result<Friendship> Create(Guid requesterId, Guid addresseeId)
    {
        if (requesterId == addresseeId)
            return Result.Failure<Friendship>(DomainErrors.CannotFriendSelf);

        return Result.Success(new Friendship
        {
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = FriendshipStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result Accept()
    {
        if (Status != FriendshipStatus.Pending)
            return Result.Failure(DomainErrors.FriendshipNotPending);

        Status = FriendshipStatus.Accepted;
        RespondedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }
}
