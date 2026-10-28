using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Queries;

public record EvalPendingOperationsResponse(int PendingCount);

public record GetEvalPendingOperationsQuery(Guid UserId) : IRequest<Result<EvalPendingOperationsResponse>>;

public sealed class GetEvalPendingOperationsQueryHandler(IGenericRepository<PendingAgentOperationState> operations)
    : IRequestHandler<GetEvalPendingOperationsQuery, Result<EvalPendingOperationsResponse>>
{
    public async Task<Result<EvalPendingOperationsResponse>> Handle(
        GetEvalPendingOperationsQuery request, CancellationToken cancellationToken)
    {
        var pendingCount = await operations.CountAsync(
            item => item.UserId == request.UserId && item.ConsumedAtUtc == null, cancellationToken);
        return Result.Success(new EvalPendingOperationsResponse(pendingCount));
    }
}
