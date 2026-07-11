using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public sealed class IdempotencyStore(OrbitDbContext context) : IIdempotencyStore
{
    public async Task<string?> FindResponseBodyAsync(Guid userId, string idempotencyKey, string requestType, CancellationToken cancellationToken)
    {
        return await context.ProcessedRequests
            .AsNoTracking()
            .Where(request => request.UserId == userId
                && request.IdempotencyKey == idempotencyKey
                && request.RequestType == requestType)
            .Select(request => request.ResponseBody)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public IIdempotencyReservation Reserve(Guid userId, string idempotencyKey, string requestType)
    {
        var record = ProcessedRequest.Create(userId, idempotencyKey, requestType);
        context.ProcessedRequests.Add(record);
        return new Reservation(record);
    }

    private sealed class Reservation(ProcessedRequest record) : IIdempotencyReservation
    {
        public void SetResponseBody(string responseBody) => record.SetResponseBody(responseBody);
    }
}
