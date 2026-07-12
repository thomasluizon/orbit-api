using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ChecklistTemplates.Queries;

public record ChecklistTemplateResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Items);

public record GetChecklistTemplatesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<ChecklistTemplateResponse>>>;

public class GetChecklistTemplatesQueryHandler(
    IGenericRepository<ChecklistTemplate> repository,
    IMemoryCache cache) : IRequestHandler<GetChecklistTemplatesQuery, Result<IReadOnlyList<ChecklistTemplateResponse>>>
{
    public async Task<Result<IReadOnlyList<ChecklistTemplateResponse>>> Handle(GetChecklistTemplatesQuery request, CancellationToken cancellationToken)
    {
        var cacheKey = ReferenceCacheKeys.ChecklistTemplates(request.UserId);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ChecklistTemplateResponse>? cached) && cached is not null)
            return Result.Success(cached);

        var templates = await repository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var result = templates
            .OrderBy(t => t.CreatedAtUtc)
            .Select(t => new ChecklistTemplateResponse(t.Id, t.Name, t.Items))
            .ToList();

        cache.Set(cacheKey, (IReadOnlyList<ChecklistTemplateResponse>)result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ReferenceCacheKeys.Ttl
        });

        return Result.Success<IReadOnlyList<ChecklistTemplateResponse>>(result);
    }
}
