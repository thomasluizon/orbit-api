using MediatR;
using Orbit.Application.Common;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Behaviors;

/// <summary>
/// Re-runs a request handler when its save throws <see cref="DbUpdateConcurrencyException"/> (a stale
/// xmin token on User/Goal/Referral), clearing change tracking between attempts so the re-run reads
/// current state. Only applies to requests marked <see cref="IConcurrencyRetryable"/>. After the last
/// attempt the conflict propagates and is surfaced as HTTP 409 by ConcurrencyExceptionHandler.
/// </summary>
public sealed class ConcurrencyRetryBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IConcurrencyRetryable)
            return await next(cancellationToken);

        return await ConcurrencyRetry.RunAsync(unitOfWork, ct => next(ct), cancellationToken);
    }
}
