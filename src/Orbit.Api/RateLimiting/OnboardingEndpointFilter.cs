using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Api.Controllers;

namespace Orbit.Api.RateLimiting;

public sealed class OnboardingEndpointFilter : IAsyncResourceFilter, IAsyncExceptionFilter, IAlwaysRunResultFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await next();
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
            return Task.CompletedTask;
        context.Result = new ObjectResult(new OnboardingController.FallbackResponse("ONBOARDING_LIMIT")) { StatusCode = 429 };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        if (context.Result is ObjectResult { StatusCode: >= 400 } result
            && result.Value is not OnboardingController.FallbackResponse)
            context.Result = new ObjectResult(new OnboardingController.FallbackResponse("ONBOARDING_LIMIT")) { StatusCode = 429 };
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
