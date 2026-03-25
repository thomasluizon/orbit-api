using MediatR;
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
    IGenericRepository<Tag> tagRepository) : IRequestHandler<GetTagsQuery, Result<IReadOnlyList<TagResponse>>>
{
    public async Task<Result<IReadOnlyList<TagResponse>>> Handle(GetTagsQuery request, CancellationToken cancellationToken)
    {
        var tags = await tagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var result = tags
            .OrderBy(t => t.Name)
            .Select(t => new TagResponse(t.Id, t.Name, t.Color))
            .ToList();

        return Result.Success<IReadOnlyList<TagResponse>>(result);
    }
}
