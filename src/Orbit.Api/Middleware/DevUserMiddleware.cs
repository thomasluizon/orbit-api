namespace Orbit.Api.Middleware;

public class DevUserMiddleware(RequestDelegate next)
{
    // Hardcoded dev user — seeded on startup
    public static readonly Guid DevUserId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public async Task InvokeAsync(HttpContext context)
    {
        context.Items["UserId"] = DevUserId;
        await next(context);
    }
}

public static class HttpContextUserExtensions
{
    public static Guid GetUserId(this HttpContext context) =>
        (Guid)context.Items["UserId"]!;
}
