using System.Reflection;
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
    private const string FailOpenPolicy = "support";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = ResolvePartitionKey(policyName, context.HttpContext, context.ActionArguments.Values);
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

            if (string.Equals(policyName, FailOpenPolicy, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

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

    private static string ResolvePartitionKey(string policyName, HttpContext context, IEnumerable<object?> actionArguments)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return $"user:{context.GetUserId()}";

        if (TryResolveAuthEmailPartitionKey(policyName, actionArguments, out var emailPartitionKey))
            return emailPartitionKey;

        return $"ip:{context.GetClientIpAddress() ?? "unknown"}";
    }

    /// <summary>
    /// For unauthenticated requests under the <c>auth</c> policy, partitions by the request's
    /// normalized email so OTP flows can't be throttled by a shared proxy IP or bypassed by
    /// rotating forwarded-IP headers. Returns false when the policy isn't <c>auth</c> or no
    /// email-bearing argument is present, so the caller can fall back to IP-based partitioning.
    /// </summary>
    public static bool TryResolveAuthEmailPartitionKey(
        string policyName,
        IEnumerable<object?> actionArguments,
        out string partitionKey)
    {
        partitionKey = string.Empty;

        if (!string.Equals(policyName, "auth", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var argument in actionArguments)
        {
            if (argument is null)
                continue;

            var emailProperty = argument.GetType().GetProperty(
                "Email",
                BindingFlags.Public | BindingFlags.Instance);

            if (emailProperty?.PropertyType != typeof(string))
                continue;

            if (emailProperty.GetValue(argument) is not string rawEmail || string.IsNullOrWhiteSpace(rawEmail))
                continue;

            partitionKey = $"auth:email:{rawEmail.Trim().ToLowerInvariant()}";
            return true;
        }

        return false;
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
