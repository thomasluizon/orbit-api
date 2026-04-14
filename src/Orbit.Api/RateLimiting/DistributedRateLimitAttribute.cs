using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DistributedRateLimitAttribute(string policyName) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new DistributedRateLimitFilter(
            policyName,
            serviceProvider.GetRequiredService<IDistributedRateLimitService>());
}

public sealed class DistributedRateLimitFilter(
    string policyName,
    IDistributedRateLimitService distributedRateLimitService) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = ResolvePartitionKey(context.HttpContext);
        var decision = await distributedRateLimitService.TryAcquireAsync(policyName, partitionKey, context.HttpContext.RequestAborted);

        if (!decision.Allowed)
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                1,
                (int)Math.Ceiling((decision.WindowEndsAtUtc - DateTime.UtcNow).TotalSeconds)).ToString();
            context.Result = new ObjectResult(new
            {
                error = "Too many requests",
                limit = decision.PermitLimit,
                count = decision.CurrentCount,
                retryAfterUtc = decision.WindowEndsAtUtc
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
            return;
        }

        await next();
    }

    private static string ResolvePartitionKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return $"user:{context.GetUserId()}";

        return $"ip:{context.GetClientIpAddress() ?? "unknown"}";
    }
}
