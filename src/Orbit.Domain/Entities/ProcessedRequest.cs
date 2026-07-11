using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Idempotency ledger for client-initiated mutations. Records that a request carrying a given client
/// <c>Idempotency-Key</c> (a mobile offline-queue mutation id) was processed for a user and stores the
/// serialized response, so a replay — a retry after a lost network ACK, routine on mobile — returns the
/// original outcome instead of re-executing the mutation. See thomasluizon/orbit-ui-mobile#243.
/// </summary>
public class ProcessedRequest : Entity
{
    public Guid UserId { get; private set; }

    public string IdempotencyKey { get; private set; } = "";

    public string RequestType { get; private set; } = "";

    public string ResponseBody { get; private set; } = "";

    public DateTime CreatedAtUtc { get; private set; }

    private ProcessedRequest() { }

    public static ProcessedRequest Create(Guid userId, string idempotencyKey, string requestType)
    {
        return new ProcessedRequest
        {
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            RequestType = requestType,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    public void SetResponseBody(string responseBody)
    {
        ResponseBody = responseBody;
    }
}
