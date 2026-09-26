using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Orbit.Application.Common;

namespace Orbit.Api.Idempotency;

/// <summary>
/// Reads the <c>Idempotency-Key</c> header and authenticated user id from the current HTTP request.
/// Only requests that carry the header (the mobile offline queue's replayable mutations) opt into
/// idempotency; every read and un-keyed request bypasses it.
/// </summary>
public sealed class HttpIdempotencyContext(IHttpContextAccessor httpContextAccessor) : IIdempotencyContext
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const int MaxKeyLength = 200;
    private readonly Dictionary<string, int> _nextOrdinalByType = new(StringComparer.Ordinal);
    private readonly object _ordinalLock = new();

    public int NextRequestOrdinal(string requestType)
    {
        lock (_ordinalLock)
        {
            _nextOrdinalByType.TryGetValue(requestType, out var ordinal);
            _nextOrdinalByType[requestType] = checked(ordinal + 1);
            return ordinal;
        }
    }

    public bool TryGetRequestKey(out Guid userId, [NotNullWhen(true)] out string idempotencyKey)
    {
        userId = Guid.Empty;
        idempotencyKey = "";

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return false;

        var key = httpContext.Request.Headers[IdempotencyKeyHeaderName].ToString().Trim();
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            return false;

        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out userId))
            return false;

        idempotencyKey = key;
        return true;
    }
}
