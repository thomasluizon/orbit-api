using Orbit.Api.Extensions;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Middleware;

internal sealed class RequestCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var slowQueries = SlowQueryCommandInterceptor.BeginRequest();
        var incomingRequestId = context.GetIncomingRequestId();
        if (!string.IsNullOrWhiteSpace(incomingRequestId))
            context.TraceIdentifier = incomingRequestId;

        context.Response.Headers[Orbit.Api.Extensions.HttpContextExtensions.RequestIdHeaderName] = context.GetRequestId();

        await next(context);
    }
}
