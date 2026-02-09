using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Queries;

public record GetUserFactsQuery(Guid UserId) : IRequest<IReadOnlyList<UserFactDto>>;

public record UserFactDto(
    Guid Id,
    string FactText,
    string? Category,
    DateTime ExtractedAtUtc,
    DateTime? UpdatedAtUtc);

public class GetUserFactsQueryHandler(
    IGenericRepository<UserFact> userFactRepository) : IRequestHandler<GetUserFactsQuery, IReadOnlyList<UserFactDto>>
{
    public async Task<IReadOnlyList<UserFactDto>> Handle(GetUserFactsQuery request, CancellationToken cancellationToken)
    {
        var facts = await userFactRepository.FindAsync(
            f => f.UserId == request.UserId,
            cancellationToken);

        return facts
            .OrderByDescending(f => f.ExtractedAtUtc)
            .Select(f => new UserFactDto(
                f.Id,
                f.FactText,
                f.Category,
                f.ExtractedAtUtc,
                f.UpdatedAtUtc))
            .ToList();
    }
}
