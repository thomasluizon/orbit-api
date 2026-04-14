using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Queries;

public record GetUserFactsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<UserFactDto>>>;

public record UserFactDto(
    Guid Id,
    string FactText,
    string? Category,
    DateTime ExtractedAtUtc,
    DateTime? UpdatedAtUtc);

public class GetUserFactsQueryHandler(
    IGenericRepository<UserFact> userFactRepository,
    IPayGateService payGate) : IRequestHandler<GetUserFactsQuery, Result<IReadOnlyList<UserFactDto>>>
{
    public async Task<Result<IReadOnlyList<UserFactDto>>> Handle(GetUserFactsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanReadUserFacts(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<IReadOnlyList<UserFactDto>>();

        var facts = await userFactRepository.FindAsync(
            f => f.UserId == request.UserId,
            cancellationToken);

        var result = facts
            .OrderByDescending(f => f.ExtractedAtUtc)
            .Select(f => new UserFactDto(
                f.Id,
                f.FactText,
                f.Category,
                f.ExtractedAtUtc,
                f.UpdatedAtUtc))
            .ToList();

        return Result.Success<IReadOnlyList<UserFactDto>>(result);
    }
}
