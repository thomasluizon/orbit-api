using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Orbit.Application.Common;

namespace Orbit.Api.Idempotency;

/// <summary>
/// Reads the <c>Idempotency-Key</c> header and authenticated user id from the current HTTP request.
/// Only requests that carry the header (the mobile offline queue's replayable mutations) opt into
/// idempotency; every read and un-keyed request bypasses it. See thomasluizon/orbit-ui-mobile#243.
/// </summary>
public sealed class HttpIdempotencyContext(IHttpContextAccessor httpContextAccessor) : IIdempotencyContext
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const int MaxKeyLength = 200;

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
