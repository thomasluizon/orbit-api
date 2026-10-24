using Orbit.Api.Hosting;

namespace Orbit.Api.Middleware;

public sealed class ShutdownDrainMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ShutdownDrain drain)
    {
        var requestAborted = context.RequestAborted;
        using var lease = drain.TryEnter(requestAborted);
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        context.RequestAborted = lease.Token;
        try
        {
            await next(context);
        }
        finally
        {
            context.RequestAborted = requestAborted;
        }
    }
}
