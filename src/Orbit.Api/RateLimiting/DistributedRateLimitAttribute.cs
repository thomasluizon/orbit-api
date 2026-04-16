using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DistributedRateLimitAttribute(string policyName) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new DistributedRateLimitFilter(
            policyName,
            serviceProvider.GetRequiredService<IDistributedRateLimitService>(),
            serviceProvider.GetRequiredService<ILogger<DistributedRateLimitFilter>>());
}

public sealed partial class DistributedRateLimitFilter(
    string policyName,
    IDistributedRateLimitService distributedRateLimitService,
    ILogger<DistributedRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = ResolvePartitionKey(context.HttpContext);
        DistributedRateLimitDecision decision;

        try
        {
            decision = await distributedRateLimitService.TryAcquireAsync(
                policyName,
                partitionKey,
                context.HttpContext.RequestAborted);
        }
        catch (Exception exception)
        {
            LogRateLimitEvaluationFailed(
                logger,
                policyName,
                partitionKey,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.GetRequestId(),
                exception);
            throw;
        }

        if (!decision.Allowed)
        {
            var retryAfterSeconds = Math.Max(
                1,
                (int)Math.Ceiling((decision.WindowEndsAtUtc - DateTime.UtcNow).TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                1,
                retryAfterSeconds).ToString();
            context.HttpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = context.HttpContext.GetRequestId();
            LogRateLimitRejected(
                logger,
                policyName,
                partitionKey,
                decision.CurrentCount,
                decision.PermitLimit,
                retryAfterSeconds,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.GetRequestId());
            context.Result = new ObjectResult(new
            {
                error = "Too many requests",
                requestId = context.HttpContext.GetRequestId(),
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

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Rate limit rejected. Policy={PolicyName} PartitionKey={PartitionKey} Count={CurrentCount}/{PermitLimit} RetryAfterSeconds={RetryAfterSeconds} {Method} {Path} RequestId={RequestId}")]
    private static partial void LogRateLimitRejected(
        ILogger logger,
        string policyName,
        string partitionKey,
        int currentCount,
        int permitLimit,
        int retryAfterSeconds,
        string method,
        string path,
        string requestId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Rate limit evaluation failed. Policy={PolicyName} PartitionKey={PartitionKey} {Method} {Path} RequestId={RequestId}")]
    private static partial void LogRateLimitEvaluationFailed(
        ILogger logger,
        string policyName,
        string partitionKey,
        string method,
        string path,
        string requestId,
        Exception exception);
}
