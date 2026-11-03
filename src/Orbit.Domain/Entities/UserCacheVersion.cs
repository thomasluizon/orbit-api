using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class UserCacheVersion : Entity
{
    public long Version { get; private set; }
    private UserCacheVersion() { }
    public static UserCacheVersion Create(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A cache version requires a user.");
        return new UserCacheVersion { Id = userId, Version = 1 };
    }
    public void Increment() => Version = checked(Version + 1);
}
