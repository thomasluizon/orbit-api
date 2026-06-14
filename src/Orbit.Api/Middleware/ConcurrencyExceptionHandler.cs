using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Extensions;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

/// <summary>
/// Maps an optimistic-concurrency conflict on an xmin-tokened entity to HTTP 409 instead of letting
/// it fall through to the 500 catch-all. Counter and progress mutations retry in their handlers and
/// never reach here; this is the safety net for plain edits (e.g. a goal or profile field saved
/// from two tabs at once), where a clean conflict is the correct outcome to surface to the client.
/// </summary>
internal sealed partial class ConcurrencyExceptionHandler(ILogger<ConcurrencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
            return false;

        LogConcurrencyConflict(logger, httpContext.Request.Method, httpContext.Request.Path, httpContext.GetRequestId());

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = httpContext.GetRequestId();

        await httpContext.Response.WriteAsJsonAsync(
            ErrorMessages.ConcurrentUpdateConflict.ToErrorBody(), cancellationToken);

        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Concurrency conflict on {Method} {Path}. RequestId={RequestId}")]
    private static partial void LogConcurrencyConflict(ILogger logger, string method, string path, string requestId);
}
