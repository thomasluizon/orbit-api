using MediatR;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;

namespace Orbit.Application.Proactive.Queries;

public sealed record ProactiveItemStatusResponse(Guid Id, string Status);

public sealed record GetProactiveItemStatusQuery(Guid UserId, Guid ItemId) : IRequest<Result<ProactiveItemStatusResponse?>>;

public sealed class GetProactiveItemStatusQueryHandler(IProactiveEngine engine)
    : IRequestHandler<GetProactiveItemStatusQuery, Result<ProactiveItemStatusResponse?>>
{
    public async Task<Result<ProactiveItemStatusResponse?>> Handle(GetProactiveItemStatusQuery request, CancellationToken cancellationToken)
    {
        var active = await engine.IsItemActiveAsync(request.UserId, request.ItemId, cancellationToken);
        return Result.Success(active.HasValue
            ? new ProactiveItemStatusResponse(request.ItemId, active.Value ? "active" : "ended")
            : null);
    }
}
