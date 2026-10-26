using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Controllers;
using Orbit.Api.Controllers;

namespace Orbit.Api.Middleware;

public sealed class AnonymousOnboardingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType()
            == typeof(OnboardingController))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Request.Headers.Remove("Authorization");
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        return next(context);
    }
}
