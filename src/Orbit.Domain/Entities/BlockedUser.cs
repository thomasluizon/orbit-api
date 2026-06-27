using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class BlockedUser : Entity
{
    public Guid BlockerId { get; private set; }
    public Guid BlockedId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private BlockedUser() { }

    public static Result<BlockedUser> Create(Guid blockerId, Guid blockedId)
    {
        if (blockerId == blockedId)
            return Result.Failure<BlockedUser>(DomainErrors.CannotBlockSelf);

        return Result.Success(new BlockedUser
        {
            BlockerId = blockerId,
            BlockedId = blockedId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
