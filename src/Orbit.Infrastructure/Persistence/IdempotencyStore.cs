using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public sealed class IdempotencyStore(OrbitDbContext context) : IIdempotencyStore
{
    public async Task<string?> FindResponseBodyAsync(Guid userId, string idempotencyKey, string requestType,
        int requestOrdinal, CancellationToken cancellationToken)
    {
        return await context.ProcessedRequests
            .AsNoTracking()
            .Where(request => request.UserId == userId
                && request.IdempotencyKey == idempotencyKey
                && request.RequestType == requestType
                && request.RequestOrdinal == requestOrdinal)
            .Select(request => request.ResponseBody)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public IIdempotencyReservation Reserve(Guid userId, string idempotencyKey, string requestType, int requestOrdinal)
    {
        var record = ProcessedRequest.Create(userId, idempotencyKey, requestType, requestOrdinal);
        context.ProcessedRequests.Add(record);
        return new Reservation(context, record);
    }

    private sealed class Reservation(OrbitDbContext context, ProcessedRequest record) : IIdempotencyReservation
    {
        public void SetResponseBody(string responseBody)
        {
            if (context.Entry(record).State == EntityState.Detached)
                context.ProcessedRequests.Attach(record);
            record.SetResponseBody(responseBody);
        }
    }
}
