using Orbit.Api.Uploads;

namespace Orbit.Api.Middleware;

public sealed class UploadObjectKeyValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.StartsWithSegments("/api/uploads/object") &&
            !UploadObjectKey.TryCreate(
                context.Request.RouteValues["userId"] as string,
                context.Request.RouteValues["fileName"] as string,
                out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
