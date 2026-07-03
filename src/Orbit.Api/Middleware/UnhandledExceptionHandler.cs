using Microsoft.AspNetCore.Diagnostics;
using Orbit.Api.Extensions;
using Sentry;

namespace Orbit.Api.Middleware;

internal sealed partial class UnhandledExceptionHandler(
    ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    private const int ClientClosedRequestStatusCode = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            if (!httpContext.Response.HasStarted)
                httpContext.Response.StatusCode = ClientClosedRequestStatusCode;
            return true;
        }

        var requestId = httpContext.GetRequestId();
        var method = httpContext.Request.Method;
        var path = httpContext.Request.Path.ToString();
        var clientIp = httpContext.GetClientIpAddress();
        var userId = ResolveUserId(httpContext);

        LogUnhandledException(logger, method, path, requestId, clientIp, userId, exception);

        SentrySdk.CaptureException(exception);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = httpContext.GetRequestId();

        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = "Unexpected server error",
            requestId = httpContext.GetRequestId(),
            status = StatusCodes.Status500InternalServerError
        }, cancellationToken);

        return true;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Unhandled exception for {Method} {Path}. RequestId={RequestId} ClientIp={ClientIp} UserId={UserId}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        string method,
        string path,
        string requestId,
        string? clientIp,
        string? userId,
        Exception exception);

    private static string? ResolveUserId(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
            return null;

        try
        {
            return httpContext.User.GetUserId().ToString();
        }
        catch
        {
            return null;
        }
    }
}
