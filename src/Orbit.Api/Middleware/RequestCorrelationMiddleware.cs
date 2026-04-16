using Orbit.Api.Extensions;

namespace Orbit.Api.Middleware;

internal sealed class RequestCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incomingRequestId = context.GetIncomingRequestId();
        if (!string.IsNullOrWhiteSpace(incomingRequestId))
            context.TraceIdentifier = incomingRequestId;

        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = context.GetRequestId();

        await next(context);
    }
}
