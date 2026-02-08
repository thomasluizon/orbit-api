using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Queries;

public record GetTagsQuery(Guid UserId) : IRequest<IReadOnlyList<Tag>>;

public class GetTagsQueryHandler(
    IGenericRepository<Tag> tagRepository) : IRequestHandler<GetTagsQuery, IReadOnlyList<Tag>>
{
    public async Task<IReadOnlyList<Tag>> Handle(GetTagsQuery request, CancellationToken cancellationToken)
    {
        return await tagRepository.FindAsync(t => t.UserId == request.UserId, cancellationToken);
    }
}
