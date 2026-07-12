using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Queries;

public record TagResponse(
    Guid Id,
    string Name,
    string Color);

public record GetTagsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<TagResponse>>>;

public class GetTagsQueryHandler(
    IGenericRepository<Tag> tagRepository,
    IMemoryCache cache) : IRequestHandler<GetTagsQuery, Result<IReadOnlyList<TagResponse>>>
{
    public async Task<Result<IReadOnlyList<TagResponse>>> Handle(GetTagsQuery request, CancellationToken cancellationToken)
    {
        var cacheKey = ReferenceCacheKeys.Tags(request.UserId);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<TagResponse>? cached) && cached is not null)
            return Result.Success(cached);

        var tags = await tagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var result = tags
            .OrderBy(t => t.Name)
            .Select(t => new TagResponse(t.Id, t.Name, t.Color))
            .ToList();

        cache.Set(cacheKey, (IReadOnlyList<TagResponse>)result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ReferenceCacheKeys.Ttl
        });

        return Result.Success<IReadOnlyList<TagResponse>>(result);
    }
}
