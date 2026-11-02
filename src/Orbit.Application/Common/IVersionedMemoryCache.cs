using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Application.Common;

public interface IUserCacheVersions
{
    Task<long> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task BumpAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IVersionedMemoryCache : IMemoryCache
{
    IUserCacheVersions Versions { get; }
}
