using Orbit.Api.Branding;

namespace Orbit.Api.Middleware;

public sealed class BrandAssetsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && BrandAssets.TryRead(context.Request.Path.Value ?? "") is { } image)
        {
            context.Response.ContentType = image.ContentType;
            context.Response.ContentLength = image.Content.Length;
            context.Response.Headers.CacheControl = $"public, max-age={BrandAssets.CacheSeconds}";
            if (HttpMethods.IsGet(context.Request.Method))
                await context.Response.Body.WriteAsync(image.Content, context.RequestAborted);
            return;
        }

        await next(context);
    }
}
