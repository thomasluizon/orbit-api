using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public record ClearEvalPendingOperationsResponse(int CancelledCount);

public record ClearEvalPendingOperationsCommand(Guid UserId)
    : IRequest<Result<ClearEvalPendingOperationsResponse>>, IConcurrencyRetryable;

public sealed class ClearEvalPendingOperationsCommandHandler(
    IGenericRepository<PendingAgentOperationState> operations,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ClearEvalPendingOperationsCommand, Result<ClearEvalPendingOperationsResponse>>
{
    public async Task<Result<ClearEvalPendingOperationsResponse>> Handle(
        ClearEvalPendingOperationsCommand request, CancellationToken cancellationToken)
    {
        var pending = await operations.FindTrackedAsync(
            item => item.UserId == request.UserId && item.ConsumedAtUtc == null, cancellationToken);
        var cancelledAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var cancelledCount = pending.Count(item => item.CancelForAccountCleanup(request.UserId, cancelledAtUtc));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(new ClearEvalPendingOperationsResponse(cancelledCount));
    }
}
